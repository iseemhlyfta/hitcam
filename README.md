# HitCam

**Камера iPhone как веб-камера для Windows 11** — открытая локальная замена iVCam.
**Use your iPhone camera as a Windows 11 webcam** — an open, local-only alternative to iVCam.

- Полностью локально: телефон подключается напрямую к ПК по Wi-Fi. Никаких серверов, аккаунтов и телеметрии.
- Подключение: IP-адрес, QR-код, PIN при первом сопряжении.
- До 1080p60, аппаратный H.264, низкая задержка.
- Управление с телефона и с ПК: объектив, зум, фокус, экспозиция, фонарик, зеркало, поворот, качество.

## Статус / Status

| Часть | Состояние |
|---|---|
| Протокол (`docs/protocol.md`) | ✅ v1 |
| ПК: сервер, сопряжение, приём потока, статистика | ✅ работает, 23 теста |
| ПК: декодирование и превью | ⏳ фаза 3 |
| ПК: виртуальная камера (MF, Windows 11) | ⏳ фаза 0/4 — нужен MSVC + Windows SDK |
| iOS: захват, H.264, подключение, PIN, QR, управление | 🧪 написано, сборка только в CI (нет Mac) |
| Автопоиск в сети (Bonjour) | ⏳ фаза 6 |
| Установщик | ⏳ фаза 8 |

## Как это устроено

```
iPhone ──(Wi-Fi, TCP 47800, H.264)──▶ HitCam для ПК ──(общая память)──▶ виртуальная камера Windows 11
```

- `ios/` — приложение для iPhone (Swift, SwiftUI, iOS 17+). Проект генерируется [XcodeGen](https://github.com/yonaskolb/XcodeGen) из `project.yml`.
- `windows/HitCam.Core` — протокол, сопряжение, TCP-сервер (.NET 10).
- `windows/HitCam.Desktop` — приложение для ПК (Avalonia).
- `windows/HitCam.FakePhone` — эмулятор телефона для разработки без iPhone.
- `windows/HitCam.VCam` — виртуальная камера (C++, Media Foundation) — в работе.

## Разработка

```powershell
cd windows
dotnet test HitCam.Core.Tests
dotnet run --project HitCam.Desktop
# во втором терминале — эмулятор телефона (PIN с экрана ПК):
dotnet run --project HitCam.FakePhone -- 127.0.0.1 47800
```

iOS собирается в GitHub Actions (`.github/workflows/ios.yml`): артефакт `HitCam-unsigned.ipa`.
Установка на iPhone без Mac — см. [docs/install-ios.md](docs/install-ios.md).

## Лицензия / License

MIT — см. [LICENSE](LICENSE).
