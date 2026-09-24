import SwiftUI

/// The PC app's dark palette and building blocks (docs: the HitCam design mockup), so both apps look alike.
enum HC {
    static let ground = Color(hex: 0x1F1F1E)
    static let surface = Color(hex: 0x2A2A28)
    static let surface2 = Color(hex: 0x323230)
    static let border = Color(hex: 0x3D3C39)
    static let text = Color(hex: 0xF2F1EE)
    static let text2 = Color(hex: 0xB3B0A8)
    static let accent = Color(hex: 0xB69CFF)
    static let onAccent = Color(hex: 0x1B0F33)
    static let accentSoft = Color(hex: 0x2E2345)
    static let accentSoftText = Color(hex: 0xCDB8FF)
    static let ok = Color(hex: 0x5CCB8F)
    static let okSoft = Color(hex: 0x1D3328)
    static let live = Color(hex: 0xF2553A)
    static let danger = Color(hex: 0xFF8A80)
    static let dangerSoft = Color(hex: 0x3A2220)
    /// Dark glass over the camera picture.
    static let overlay = Color(hex: 0x0E0E0D).opacity(0.72)
}

extension Color {
    init(hex: UInt32) {
        self.init(red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255, blue: Double(hex & 0xFF) / 255)
    }
}

/// The HitCam logo from assets/logo.
struct LogoMark: View {
    var size: CGFloat = 48

    var body: some View {
        Image("Logo")
            .resizable()
            .interpolation(.high)
            .scaledToFit()
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}

/// A rounded surface with a hairline border.
struct CardModifier: ViewModifier {
    var padding: CGFloat = 18

    func body(content: Content) -> some View {
        content
            .padding(padding)
            .background(HC.surface, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(HC.border))
    }
}

extension View {
    func card(padding: CGFloat = 18) -> some View { modifier(CardModifier(padding: padding)) }
}

struct PrimaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.body.weight(.semibold))
            .frame(maxWidth: .infinity, minHeight: 48)
            .foregroundStyle(HC.onAccent)
            .background(HC.accent.opacity(configuration.isPressed ? 0.8 : 1), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .opacity(isEnabled ? 1 : 0.4)
    }
}

struct SecondaryButtonStyle: ButtonStyle {
    var fullWidth = true

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.body)
            .frame(maxWidth: fullWidth ? .infinity : nil, minHeight: 44)
            .padding(.horizontal, fullWidth ? 0 : 18)
            .foregroundStyle(HC.text)
            .background(configuration.isPressed ? HC.border : HC.surface2, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(HC.border))
    }
}

/// Round icon button over the camera picture.
struct OverlayIconButton: View {
    let systemImage: String
    let label: String
    var tint: Color = HC.text
    var active = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.system(size: 17, weight: .semibold))
                .foregroundStyle(active ? HC.onAccent : tint)
                .frame(width: 44, height: 44)
                .background(active ? HC.accent : HC.overlay, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        }
        .accessibilityLabel(label)
    }
}

/// A status pill: dot + text.
struct Pill: View {
    let text: String
    var dot: Color? = nil
    var foreground: Color = HC.text
    var background: Color = HC.overlay

    var body: some View {
        HStack(spacing: 8) {
            if let dot { Circle().fill(dot).frame(width: 8, height: 8) }
            Text(text).font(.caption.weight(.semibold)).monospacedDigit()
        }
        .foregroundStyle(foreground)
        .padding(.horizontal, 10)
        .frame(height: 28)
        .background(background, in: Capsule())
    }
}

/// Segmented control like the PC app's lens picker.
struct Segmented<ID: Hashable>: View {
    let options: [(id: ID, label: String)]
    let selection: ID
    let onSelect: (ID) -> Void

    var body: some View {
        HStack(spacing: 2) {
            ForEach(options, id: \.id) { option in
                let selected = option.id == selection
                Button { onSelect(option.id) } label: {
                    Text(option.label)
                        .font(.footnote.weight(selected ? .semibold : .regular))
                        .lineLimit(1)
                        .minimumScaleFactor(0.8)
                        .frame(maxWidth: .infinity, minHeight: 34)
                        .foregroundStyle(HC.text)
                        .background(selected ? HC.surface : .clear, in: RoundedRectangle(cornerRadius: 6, style: .continuous))
                        .shadow(color: .black.opacity(selected ? 0.25 : 0), radius: 1, y: 1)
                }
                .accessibilityAddTraits(selected ? .isSelected : [])
            }
        }
        .padding(2)
        .background(HC.surface2, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(HC.border))
    }
}

/// A 44-pt tile with an icon and a label; highlighted in violet when on.
struct ToggleTile: View {
    let systemImage: String
    let label: String
    var isOn = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 6) {
                Image(systemName: systemImage).font(.system(size: 14, weight: .semibold))
                Text(label).font(.footnote.weight(isOn ? .semibold : .regular)).lineLimit(1).minimumScaleFactor(0.8)
            }
            .frame(maxWidth: .infinity, minHeight: 44)
            .foregroundStyle(isOn ? HC.accentSoftText : HC.text)
            .background(isOn ? HC.accentSoft : HC.surface2, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(isOn ? HC.accent : HC.border))
        }
        .accessibilityAddTraits(isOn ? .isSelected : [])
    }
}

/// Caption above a control, like the PC settings panel.
struct FieldLabel<Trailing: View>: View {
    let title: String
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack {
            Text(title).font(.caption).foregroundStyle(HC.text2)
            Spacer()
            trailing
        }
    }
}

extension FieldLabel where Trailing == EmptyView {
    init(_ title: String) {
        self.title = title
        trailing = EmptyView()
    }
}

/// A slider with its caption and current value.
struct SliderRow<Accessory: View>: View {
    let title: String
    let valueText: String
    let value: Binding<Double>
    let range: ClosedRange<Double>
    @ViewBuilder var accessory: Accessory

    var body: some View {
        VStack(spacing: 4) {
            FieldLabel(title: title) {
                HStack(spacing: 8) {
                    accessory
                    Text(valueText).font(.caption.weight(.semibold)).monospacedDigit().foregroundStyle(HC.text)
                }
            }
            Slider(value: value, in: range).tint(HC.accent)
        }
    }
}

extension SliderRow where Accessory == EmptyView {
    init(title: String, valueText: String, value: Binding<Double>, range: ClosedRange<Double>) {
        self.init(title: title, valueText: valueText, value: value, range: range) { EmptyView() }
    }
}

/// A small pill button such as the focus panel's "Auto".
struct ChipButton: View {
    let title: String
    var isOn: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(title)
                .font(.caption2.weight(.semibold))
                .padding(.horizontal, 10)
                .frame(height: 24)
                .foregroundStyle(isOn ? HC.accentSoftText : HC.text2)
                .background(isOn ? HC.accentSoft : HC.surface2, in: Capsule())
        }
        .accessibilityAddTraits(isOn ? .isSelected : [])
    }
}

/// Message box: red for errors.
struct NoticeBox: View {
    let text: String
    var isError = true

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: isError ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                .foregroundStyle(isError ? HC.danger : HC.ok)
            Text(text).font(.footnote).foregroundStyle(HC.text).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(12)
        .background(isError ? HC.dangerSoft : HC.okSoft, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }
}
