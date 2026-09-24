<img width="1920" height="960" alt="hitcam2_00000" src="https://github.com/user-attachments/assets/8b6087c7-444a-4e34-bef6-6c3792e59357" />

# HitCam

**Камера iPhone или Android-телефона как веб-камера для Windows 11** — открытая локальная замена iVCam.
**Use your iPhone or Android phone camera as a Windows 11 webcam** — an open, local-only alternative to iVCam.

- Полностью локально: телефон подключается напрямую к ПК по Wi-Fi. Никаких серверов, аккаунтов и телеметрии.
- Подключение: IP-адрес, QR-код, PIN при первом сопряжении.
- До 1080p60, аппаратный H.264, низкая задержка.
- Управление с телефона и с ПК (панель «Камера»): объектив, качество, зум, экспозиция и её фиксация, баланс белого,
  фокус, стабилизация, фонарик, зеркало, поворот.
- ИИ-шумоподавление на видеокарте ПК (NVIDIA RTX).

## Статус / Status

| Часть | Состояние |
|---|---|
| Протокол (`docs/protocol.md`) | ✅ v1 |
| ПК: сервер, сопряжение, приём потока, статистика | ✅ работает, покрыто тестами |
| ПК: настройки камеры телефона | ✅ работает, покрыто тестами |
| ПК: декодирование H.264 | ✅ работает (Media Foundation) |
| ПК: виртуальная камера (MF, Windows 11) | ✅ работает (`MFCreateVirtualCamera`) |
| ПК: ИИ-шумоподавление (NVIDIA) | 🧪 экспериментально: на сжатом видео пока не помогает, идёт доработка |
| ПК: превью в окне, дизайн (светлая и тёмная тема) | ✅ |
| iOS: захват, H.264, подключение, PIN, QR, управление | 🧪 написано, сборка только в CI (нет Mac) |
| Android: захват, H.264, подключение, PIN, QR, управление | 🧪 в разработке, APK собирается в CI |
| Автопоиск в сети (Bonjour) | ⏳ фаза 6 |
| Установка на ПК (ярлыки, «Приложения» Windows) | ✅ `windows\install.ps1` |

## Как это устроено

```
iPhone / Android ──(Wi-Fi, TCP 47800, H.264)──▶ HitCam для ПК ──(общая память)──▶ виртуальная камера Windows 11
```

- `ios/` — приложение для iPhone (Swift, SwiftUI, iOS 17+). Проект генерируется [XcodeGen](https://github.com/yonaskolb/XcodeGen) из `project.yml`.
- `android/` — приложение для Android (Android 8.0+), проект Gradle с модулем `app`.
- `windows/HitCam.Core` — протокол, сопряжение, TCP-сервер (.NET 10).
- `windows/HitCam.Desktop` — приложение для ПК (Avalonia).
- `windows/HitCam.FakePhone` — эмулятор телефона для разработки без телефона.
- `windows/HitCam.VCam` — виртуальная камера и декодер (C++, Media Foundation). Источник камеры загружает служба
  «Windows Camera Frame Server»; кадры приходят к нему из HitCam через общую память `Global\HitCamVirtualCameraFrames`.

## Установка на ПК

```powershell
powershell -ExecutionPolicy Bypass -File windows\install.ps1
```

Собирает HitCam и ставит его для текущего пользователя в `%LOCALAPPDATA%\Programs\HitCam` (без прав администратора),
добавляет ярлыки в меню «Пуск» и на рабочий стол и запись в «Параметры → Приложения» (оттуда же удаление).
Повторный запуск скрипта обновляет установленную версию. Второй запуск HitCam просто показывает уже открытое окно.

Готовая сборка без Visual Studio — `HitCam-windows-x64.zip` на странице Releases: распакуйте и запустите `HitCam.exe`.

## Установка на телефон

- **iPhone** (iOS 17+): `HitCam-unsigned.ipa` ставится с Windows через Sideloadly — см. [docs/install-ios.md](docs/install-ios.md).
- **Android** (8.0+): скачайте `HitCam-android.apk` со страницы Releases прямо на телефон и установите —
  см. [docs/install-android.md](docs/install-android.md).

## Виртуальная камера

При первом запуске HitCam для ПК нажмите **«Установить камеру»** и подтвердите запрос администратора:
`HitCamVCam.dll` копируется в `C:\Program Files\HitCam` и регистрируется. Дальше камера **HitCam** появляется
в Zoom, Discord, Teams, OBS и браузерах, пока открыт HitCam для ПК. Без телефона камера показывает тёмно-серый кадр.

Удаление: `regsvr32 /u "C:\Program Files\HitCam\HitCamVCam.dll"` (от администратора), затем удалить папку.

## ИИ-шумоподавление (NVIDIA RTX)

В панели «Камера» есть переключатель **«ИИ-шумоподавление»**: нейросеть NVIDIA (эффект «Denoising» из NVIDIA Video
Effects SDK) убирает шум, не размывая детали. Работает на видеокарте ПК после декодирования и телефон не нагружает;
на RTX 3070 около 5–6 мс на кадр 1080p. Три режима:

- **Быстрое** — мягкая модель, только когда в кадре есть шум (он измеряется в каждом кадре); чистую картинку не трогает;
- **Общее** (по умолчанию) — мягкая модель на каждом кадре: убирает зерно и шум сжатия, сохраняет текстуру;
- **Максимальное** — сильная модель на каждом кадре: чище всего, но сглаживает мелкие детали.
Сравнение на текстурной сцене: `HitCamVCamTest.exe --denoise-scene`.

Нужны видеокарта NVIDIA RTX (20xx–50xx) и бесплатный компонент NVIDIA **Video Effects SDK redistributable** для своего
поколения карт: <https://www.nvidia.com/en-us/geforce/broadcasting/broadcast-sdk/resources/>. Без него переключатель
неактивен. Файлы NVIDIA в репозиторий не входят (лицензия NVIDIA); в `windows/HitCam.VCam/third_party/nvvfx` лежат
только заголовки и загрузчики SDK под MIT.

Проверка без телефона: `windows\HitCam.VCam\build\Release\HitCamVCamTest.exe --denoise`.

## Разработка

Нужны .NET 10 SDK и Visual Studio с «Разработкой классических приложений на C++» (MSVC, Windows 11 SDK):
сборка `HitCam.Desktop` сама собирает `HitCam.VCam` через CMake. Без C++: `-p:SkipVirtualCamera=true`.

```powershell
cd windows
dotnet test HitCam.Core.Tests
dotnet test HitCam.Desktop.Tests
dotnet run --project HitCam.Desktop
# во втором терминале — эмулятор телефона (PIN с экрана ПК):
dotnet run --project HitCam.FakePhone -- 127.0.0.1 47800
```

iOS собирается в GitHub Actions (`.github/workflows/ios.yml`): артефакт `HitCam-unsigned.ipa`.

Android собирается в GitHub Actions (`.github/workflows/android.yml`, JDK 21): артефакт `HitCam-android.apk`.
Локально: `cd android && ./gradlew testDebugUnitTest assembleRelease`. Релизный ключ берётся из переменных окружения
`HITCAM_KEYSTORE`, `HITCAM_KEYSTORE_PASSWORD`, `HITCAM_KEY_ALIAS`, `HITCAM_KEY_PASSWORD` (в CI — секреты
`ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`); без них APK
подписывается отладочным ключом.

Релиз: `git tag v0.1.0 && git push origin v0.1.0` — workflow **Release** собирает приложение для Windows, IPA и APK
и публикует их на странице Releases.
Установка на iPhone без Mac — см. [docs/install-ios.md](docs/install-ios.md), на Android — [docs/install-android.md](docs/install-android.md).

## Лицензия / License

MIT — см. [LICENSE](LICENSE).
