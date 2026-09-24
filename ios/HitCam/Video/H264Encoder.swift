import CoreMedia
import Foundation
import VideoToolbox

enum AnnexB {
    static let startCode: [UInt8] = [0, 0, 0, 1]

    /// Converts length-prefixed (AVCC) NAL units to start-code delimited (Annex-B) ones.
    static func fromAVCC(_ avcc: Data, nalLengthSize: Int = 4) -> Data? {
        guard (1...4).contains(nalLengthSize) else { return nil }
        var output = Data(capacity: avcc.count + 16)
        var offset = avcc.startIndex
        while offset < avcc.endIndex {
            guard avcc.endIndex - offset >= nalLengthSize else { return nil }
            var length = 0
            for i in 0..<nalLengthSize {
                length = (length << 8) | Int(avcc[offset + i])
            }
            offset += nalLengthSize
            guard length > 0, avcc.endIndex - offset >= length else { return nil }
            output.append(contentsOf: startCode)
            output.append(avcc[offset..<(offset + length)])
            offset += length
        }
        return output
    }
}

struct EncodedFrame {
    var data: Data          // Annex-B access unit; keyframes start with SPS and PPS
    var isKeyframe: Bool
    var timestampMicros: UInt64
}

/// Hardware H.264 encoder tuned for low latency: no B-frames, real-time, a keyframe every 2 seconds.
final class H264Encoder {
    private var session: VTCompressionSession?
    private let output: (EncodedFrame) -> Void
    private var forceKeyframe = true
    private let lock = NSLock()

    init(output: @escaping (EncodedFrame) -> Void) {
        self.output = output
    }

    deinit { invalidate() }

    func configure(width: Int32, height: Int32, fps: Int, bitrateKbps: Int) throws {
        invalidate()

        let specification: [CFString: Any] = [kVTVideoEncoderSpecification_EnableLowLatencyRateControl: true]
        var created: VTCompressionSession?
        let status = VTCompressionSessionCreate(
            allocator: nil,
            width: width,
            height: height,
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: specification as CFDictionary,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: nil,
            refcon: nil,
            compressionSessionOut: &created)
        guard status == noErr, let session = created else {
            throw NSError(domain: NSOSStatusErrorDomain, code: Int(status))
        }

        func set(_ key: CFString, _ value: Any) {
            VTSessionSetProperty(session, key: key, value: value as CFTypeRef)
        }
        set(kVTCompressionPropertyKey_RealTime, kCFBooleanTrue as Any)
        set(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse as Any)
        set(kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_ConstrainedHigh_AutoLevel)
        set(kVTCompressionPropertyKey_AverageBitRate, bitrateKbps * 1000)
        set(kVTCompressionPropertyKey_ExpectedFrameRate, fps)
        set(kVTCompressionPropertyKey_MaxKeyFrameInterval, fps * 2)
        set(kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, 2)
        VTCompressionSessionPrepareToEncodeFrames(session)

        lock.lock()
        self.session = session
        forceKeyframe = true
        lock.unlock()
    }

    func setBitrate(kbps: Int) {
        lock.lock()
        let session = self.session
        lock.unlock()
        guard let session else { return }
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: (kbps * 1000) as CFTypeRef)
    }

    func requestKeyframe() {
        lock.lock()
        forceKeyframe = true
        lock.unlock()
    }

    func invalidate() {
        lock.lock()
        let session = self.session
        self.session = nil
        lock.unlock()
        if let session {
            VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
            VTCompressionSessionInvalidate(session)
        }
    }

    func encode(_ sampleBuffer: CMSampleBuffer) {
        guard let imageBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        lock.lock()
        let session = self.session
        let keyframe = forceKeyframe
        forceKeyframe = false
        lock.unlock()
        guard let session else { return }

        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let timestamp = UInt64(max(0, CMTimeGetSeconds(pts)) * 1_000_000)
        let properties: CFDictionary? = keyframe ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary : nil

        VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: imageBuffer,
            presentationTimeStamp: pts,
            duration: .invalid,
            frameProperties: properties,
            infoFlagsOut: nil
        ) { [weak self] status, _, encoded in
            guard let self else { return }
            guard status == noErr, let encoded, let frame = Self.annexB(from: encoded, timestamp: timestamp) else {
                self.requestKeyframe()
                return
            }
            self.output(frame)
        }
    }

    private static func annexB(from sample: CMSampleBuffer, timestamp: UInt64) -> EncodedFrame? {
        guard let block = CMSampleBufferGetDataBuffer(sample), let format = CMSampleBufferGetFormatDescription(sample) else {
            return nil
        }

        var isKeyframe = true
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[CFString: Any]],
           let first = attachments.first,
           let notSync = first[kCMSampleAttachmentKey_NotSync] as? Bool {
            isKeyframe = !notSync
        }

        var nalLengthSize: Int32 = 4
        var parameterSetCount = 0
        CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            format, parameterSetIndex: 0, parameterSetPointerOut: nil, parameterSetSizeOut: nil,
            parameterSetCountOut: &parameterSetCount, nalUnitHeaderLengthOut: &nalLengthSize)

        var output = Data()
        if isKeyframe {
            for index in 0..<parameterSetCount {
                var pointer: UnsafePointer<UInt8>?
                var size = 0
                let status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                    format, parameterSetIndex: index, parameterSetPointerOut: &pointer, parameterSetSizeOut: &size,
                    parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
                guard status == noErr, let pointer else { return nil }
                output.append(contentsOf: AnnexB.startCode)
                output.append(pointer, count: size)
            }
        }

        // Copy out: the block buffer is not guaranteed to be contiguous.
        let totalLength = CMBlockBufferGetDataLength(block)
        var avcc = Data(count: totalLength)
        let copied = avcc.withUnsafeMutableBytes { raw in
            CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: totalLength, destination: raw.baseAddress!)
        }
        guard copied == kCMBlockBufferNoErr,
              let body = AnnexB.fromAVCC(avcc, nalLengthSize: Int(nalLengthSize)) else { return nil }
        output.append(body)
        return EncodedFrame(data: output, isKeyframe: isKeyframe, timestampMicros: timestamp)
    }
}
