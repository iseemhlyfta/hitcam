<img width="1920" height="960" alt="hitcam2_00000" src="https://github.com/user-attachments/assets/8b6087c7-444a-4e34-bef6-6c3792e59357" />

# HitCam

**Камера iPhone или Android-телефона как веб-камера для Windows 10 и 11** — открытая локальная замена iVCam.
**Use your iPhone or Android phone camera as a Windows 10/11 webcam** — an open, local-only alternative to iVCam.

- Полностью локально: телефон подключается напрямую к ПК по Wi-Fi. Никаких серверов, аккаунтов и телеметрии.
- Подключение: IP-адрес, QR-код, PIN при первом сопряжении.
- До 1080p60, аппаратный H.264, низкая задержка.
- Управление с телефона и с ПК (панель «Камера»): объектив, качество, зум, экспозиция и её фиксация, баланс белого,
  фокус, стабилизация, фонарик, зеркало, поворот.
- Обработка на видеокарте ПК: шумоподавление без шлейфов, удаление артефактов сжатия (NVIDIA RTX), цвет и резкость.

## Статус / Status

| Часть | Состояние |
|---|---|
| Протокол (`docs/protocol.md`) | ✅ v1 |
| ПК: сервер, сопряжение, приём потока, статистика | ✅ работает, покрыто тестами |
| ПК: настройки камеры телефона | ✅ работает, покрыто тестами |
| ПК: декодирование H.264 | ✅ работает (Media Foundation) |
| ПК: виртуальная камера (MF, Windows 11) | ✅ работает (`MFCreateVirtualCamera`) |
| ПК: виртуальная камера (DirectShow, Windows 10) | 🧪 новое: проверено тестом на Windows 11, ждёт проверки на Windows 10 |
| ПК: обработка картинки (Direct3D 11, NVIDIA Artifact Reduction) | 🧪 новое: проверено тестами, ждёт проверки на живом видео |
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

Каждая версия камеры ставится под своим именем (`HitCamVCam-<часть хэша>.dll`): старый файл может быть занят
программами, которые открывали камеру, и удаляется при следующем обновлении.
Удаление: `regsvr32 /u` для `C:\Program Files\HitCam\HitCamVCam-*.dll` (от администратора), затем удалить папку.

### Windows 10

В Windows 10 нет виртуальных камер Media Foundation, поэтому там HitCam ставит камеру **DirectShow** на основе
[Softcam](https://github.com/tshino/softcam) (MIT): `HitCamDShow.dll` для 64-битных программ и `HitCamDShow32.dll`
для 32-битных. Кнопка та же — «Установить камеру». Камеру видят Zoom, Discord, Teams, Skype, OBS, Chrome, Edge,
Firefox и сайты вроде Google Meet; не видят приложение «Камера» Windows и часть приложений из Microsoft Store.
Кадр всегда 1920×1080: 720p растягивается, вертикальная картинка показывается с чёрными полями.

Удаление (от администратора): `regsvr32 /u` для `HitCamDShow-*.dll` и `%WINDIR%\SysWOW64\regsvr32 /u` для
`HitCamDShow32-*.dll` в `C:\Program Files\HitCam`, затем удалить папку.
Проверить DirectShow-камеру в Windows 11 можно, запустив HitCam с переменной окружения `HITCAM_CAMERA=directshow`.

## Обработка картинки на ПК

В панели справа есть раздел **«Обработка»**. Всё считается на видеокарте ПК (Direct3D 11, любая видеокарта; без неё —
программный режим) после декодирования, телефон не нагружает. Когда всё выключено, кадры проходят без изменений.

**Шумоподавление:**
- **Временное шумоподавление** (0–100) — на неподвижных участках кадр усредняется с предыдущими, там где есть движение
  фильтр отключается, поэтому шлейфов нет и ничего не дорисовывается. Лучше всего подходит для веб-камеры.
- **Удаление артефактов сжатия (NVIDIA RTX)** — нейросеть NVIDIA «Artifact Reduction» убирает квадраты, ореолы и
  «москитный» шум сжатия H.264: «Мягко» для обычного битрейта, «Сильно» для низкого. Около 6–22 мс на кадр 1080p.
  Вертикальное видео не поддерживается. Нужны видеокарта NVIDIA RTX (20xx–50xx) и бесплатный NVIDIA
  **Video Effects SDK redistributable**: <https://www.nvidia.com/en-us/geforce/broadcasting/broadcast-sdk/resources/>.
  Файлы NVIDIA в репозиторий не входят (лицензия NVIDIA); в `windows/HitCam.VCam/third_party/nvvfx` лежат только
  заголовки и загрузчики SDK под MIT.
- **Шумоподавление камеры телефона** (Выкл / Быстрое / Качество) — шум убирает сама камера телефона ещё до сжатия, это
  даёт самую чистую картинку. Есть на Android (если камера даёт выбор); iOS управлять этим не позволяет.

**Цвет и резкость:** яркость, контраст, насыщенность, тени, света и резкость (без усиления шума); «Сбросить» возвращает
всё к исходному. Все эффекты вместе — около 3–5 мс на кадр 1080p. Настройки сохраняются.

Проверка без телефона: `windows\HitCam.VCam\build\Release\HitCamVCamTest.exe --process` (build.ps1 -Tests).

## Анализ объектов (HitCam Vision)

Раздел **«Анализ объектов»** в правой панели находит в кадре 80 видов объектов (люди, животные, транспорт, предметы)
и обводит их рамками с подписью вида «person 92%». Рамки плавные: у каждого объекта свой цвет, пока он в кадре.
Можно выбрать, какие классы искать, и порог уверенности. Переключатель «Показывать рамки в камере HitCam» добавляет
рамки в само видео — их увидят в Zoom, Discord, OBS.

Работает на ПК: нейросеть **RF-DETR** (Roboflow, Apache 2.0) через ONNX Runtime + DirectML — любая видеокарта
(NVIDIA, AMD, Intel), без неё процессор. Модель «Быстрая» (Nano) — около 9–13 мс на кадр на RTX 3070, «Точная»
(Small) — около 17 мс. Анализ идёт отдельно от видео и никогда его не замедляет. Стандартная модель входит в архив
релиза (папка `models` рядом с `HitCam.exe`); свои модели кладутся в `%LOCALAPPDATA%\HitCam\models`.

Как это устроено, теория для начинающих и дообучение на своих объектах (Python, папка `vision/`): см.
[docs/vision.md](docs/vision.md) и [vision/README.md](vision/README.md).

## Отслеживание рук

Раздел **«Отслеживание рук»** ставит точки на суставы и кончики пальцев, до двух рук сразу. Точки держатся на пальцах
и сглаживаются, чтобы не дрожать; переключатель «Соединять точки линиями» рисует «скелет» руки. Точки видны только
в превью. Работает на ПК: модели **MediaPipe Hands** (Google, Apache 2.0, в ONNX от OpenCV Zoo) через ONNX Runtime +
DirectML, около 2 мс на руку на RTX 3070. Модели входят в архив релиза (`models\hands`); `install.ps1` скачивает их
сам и проверяет SHA-256. Подробнее: [docs/vision.md](docs/vision.md#отслеживание-рук).

**Жесты:**
- **Пистолет из пальцев.** Указательный (можно вместе со средним), большой отогнут, остальные в кулаке. Резкий рывок
  кистью вверх — выстрел: вспышка у ствола, вспышка и толчок кадра.
- **Нити.** Коснитесь кончиками одинаковых пальцев двух рук — между ними натянется белая нить; она тянется за
  пальцами, пока те же пальцы не коснутся снова. Между соседними нитями — заливка-лента от цвета левой руки к цвету
  правой: у каждой заливки свои цвета, а у ленты две стороны разного цвета — перевернёте руки, увидите обратную
  (цвета первой заливки и прозрачность настраиваются).

В превью показывается всё, а что попадает в камеру «HitCam» (точки, нити, заливка, выстрел), выбирается отдельно.

## Разработка

Нужны .NET 10 SDK и Visual Studio с «Разработкой классических приложений на C++» (MSVC, Windows 11 SDK):
сборка `HitCam.Desktop` сама собирает `HitCam.VCam` через CMake. Без C++: `-p:SkipVirtualCamera=true`.

```powershell
cd windows
dotnet test HitCam.Core.Tests
dotnet test HitCam.Desktop.Tests
dotnet test HitCam.Vision.Tests
dotnet run --project HitCam.Desktop
# во втором терминале — эмулятор телефона (PIN с экрана ПК):
dotnet run --project HitCam.FakePhone -- 127.0.0.1 47800
# с настоящим видео по кругу (H.264 Annex B с разделителями кадров, x264 aud=1):
dotnet run --project HitCam.FakePhone -- 127.0.0.1 47800 --video hands.h264 --size 1280x960
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

Сторонние компоненты: Softcam (MIT), DirectShow base classes (MIT), RF-DETR и его веса Nano/Small (Apache 2.0),
модели MediaPipe Hands из OpenCV Zoo (Apache 2.0),
ONNX Runtime (MIT), DirectML.dll (Microsoft, распространяется по лицензии Microsoft для DirectML), заголовки NVIDIA
Video Effects SDK (MIT; сама среда NVIDIA ставится отдельно).
