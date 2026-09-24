<img width="1920" height="960" alt="hitcam_00000" src="https://github.com/user-attachments/assets/7dad765f-a832-4b1b-ae0d-407609e5cead" />
# HitCam

**Камера iPhone как веб-камера для Windows 11** — открытая локальная замена iVCam.
**Use your iPhone camera as a Windows 11 webcam** — an open, local-only alternative to iVCam.

- Полностью локально: телефон подключается напрямую к ПК по Wi-Fi. Никаких серверов, аккаунтов и телеметрии.
- Подключение: IP-адрес, QR-код, PIN при первом сопряжении.
- До 1080p60, аппаратный H.264, низкая задержка.
- Управление с телефона и с ПК (панель «Настройки камеры»): объектив, зум, фокус, экспозиция, фонарик, зеркало, поворот, качество.

## Статус / Status

| Часть | Состояние |
|---|---|
| Протокол (`docs/protocol.md`) | ✅ v1 |
| ПК: сервер, сопряжение, приём потока, статистика | ✅ работает, 23 теста |
| ПК: настройки камеры телефона | 🧪 написано, 7 тестов (проверено с эмулятором логики, не с iPhone) |
| ПК: декодирование H.264 | 🧪 написано (Media Foundation) |
| ПК: виртуальная камера (MF, Windows 11) | 🧪 написано (`MFCreateVirtualCamera`) |
| ПК: превью в окне, дизайн (светлая и тёмная тема) | ✅ |
| iOS: захват, H.264, подключение, PIN, QR, управление | 🧪 написано, сборка только в CI (нет Mac) |
| Автопоиск в сети (Bonjour) | ⏳ фаза 6 |
| Установка на ПК (ярлыки, «Приложения» Windows) | ✅ `windows\install.ps1` |

## Как это устроено

```
iPhone ──(Wi-Fi, TCP 47800, H.264)──▶ HitCam для ПК ──(общая память)──▶ виртуальная камера Windows 11
```

- `ios/` — приложение для iPhone (Swift, SwiftUI, iOS 17+). Проект генерируется [XcodeGen](https://github.com/yonaskolb/XcodeGen) из `project.yml`.
- `windows/HitCam.Core` — протокол, сопряжение, TCP-сервер (.NET 10).
- `windows/HitCam.Desktop` — приложение для ПК (Avalonia).
- `windows/HitCam.FakePhone` — эмулятор телефона для разработки без iPhone.
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

## Виртуальная камера

При первом запуске HitCam для ПК нажмите **«Установить камеру»** и подтвердите запрос администратора:
`HitCamVCam.dll` копируется в `C:\Program Files\HitCam` и регистрируется. Дальше камера **HitCam** появляется
в Zoom, Discord, Teams, OBS и браузерах, пока открыт HitCam для ПК. Без телефона камера показывает тёмно-серый кадр.

Удаление: `regsvr32 /u "C:\Program Files\HitCam\HitCamVCam.dll"` (от администратора), затем удалить папку.

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

Релиз: `git tag v0.1.0 && git push origin v0.1.0` — workflow **Release** собирает приложение для Windows и IPA
и публикует их на странице Releases.
Установка на iPhone без Mac — см. [docs/install-ios.md](docs/install-ios.md).

## Лицензия / License

MIT — см. [LICENSE](LICENSE).
