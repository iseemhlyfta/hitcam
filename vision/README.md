# HitCam Vision: обучение детектора на своих объектах

Здесь всё, чтобы научить детектор объектов RF-DETR находить **ваши** предметы (кружку, ключи, кота, деталь на
станке) и подключить получившуюся модель к HitCam для ПК. Как это устроено и немного теории для начинающих —
в [docs/vision.md](../docs/vision.md).

Путь целиком:

```
collect.py        кадры с камеры HitCam           data/<набор>/images/
Label Studio      рисуем рамки, экспорт COCO      data/<набор>/export/result.json
split.py          делим на train / valid / test   data/<набор>/{train,valid,test}/
train.py          дообучаем RF-DETR               runs/<запуск>/checkpoint_best_total.pth
eval.py           меряем качество на test         runs/<запуск>/eval/
export.py         ONNX + labels.json, проверка    output/models/ и %LOCALAPPDATA%\HitCam\models\
```

| Файл | Что делает |
|---|---|
| `collect.py` | сохраняет кадры с виртуальной камеры «HitCam» по пробелу или каждые N секунд |
| `split.py` | раскладывает экспорт COCO из Label Studio по train / valid / test в формате RF-DETR |
| `train.py` | дообучает RF-DETR Nano (или `--model small`) на вашем наборе |
| `eval.py` | mAP@50, mAP@50:95, точность и полнота по классам, картинки «найдено / правильный ответ» |
| `export.py` | ONNX FP32 + labels.json, сверка ONNX на DirectML с PyTorch, `--install` кладёт модель в HitCam |
| `check_onnx.py` | прогоняет любую ONNX-модель на картинках через DirectML и CPU, показывает рамки и скорость |
| `export_default.py` | экспортирует стандартную COCO-модель (80 классов), которую HitCam ставит по умолчанию |
| `tools/make_toy_dataset.py` | синтетический набор (мяч, коробка, звезда) — проверить весь путь без камеры и разметки |

## 1. Установка (один раз)

Нужны Windows 10/11, Python 3.11 (`py -3.11 --version`) и, для обучения, видеокарта NVIDIA с 6–8 ГБ памяти.
Без NVIDIA обучение на процессоре идёт в десятки раз медленнее; экспорт и проверка моделей работают везде.
Все команды ниже выполняются из папки `vision`.

```powershell
cd vision
py -3.11 -m venv .venv
.venv\Scripts\activate
python -m pip install --upgrade pip
# PyTorch ставится отдельно, со своего сервера (иначе pip поставит версию без CUDA):
pip install torch==2.14.0 torchvision==0.29.0 --index-url https://download.pytorch.org/whl/cu126
# без видеокарты NVIDIA вместо строки выше:
# pip install torch==2.14.0 torchvision==0.29.0 --index-url https://download.pytorch.org/whl/cpu
pip install -r requirements.txt
python -c "import torch; print(torch.cuda.is_available(), torch.cuda.get_device_name(0))"
```

Последняя строка должна напечатать `True` и имя видеокарты. Веса RF-DETR (~100 МБ) скачаются с серверов Roboflow
при первом запуске в `%USERPROFILE%\.roboflow\models`.

Если `collect.py` падает на `cv2.imshow` с ошибкой «The function is not implemented»: одна из зависимостей
поставила OpenCV без окон, верните обычный: `pip install --force-reinstall --no-deps opencv-python==5.0.0.93`.

## 2. Проверка без камеры: игрушечный набор (5–10 минут)

Сначала убедитесь, что весь путь работает, на синтетических картинках с готовой разметкой:

```powershell
python tools\make_toy_dataset.py              # 100 картинок: red_ball, blue_box, yellow_star
python split.py toy                           # 70 / 15 / 15
python train.py toy --epochs 20               # ~2,5 мин на RTX 3070
python eval.py toy-nano                       # mAP@50 около 1.0, точность и полнота 1.0: задача простая
python export.py toy-nano                     # output\models\toy-nano.onnx, проверка на DirectML: PASSED
python check_onnx.py output\models\toy-nano.onnx data\toy\test --save output\check
```

Посмотрите картинки в `runs\toy-nano\eval\`: зелёные рамки — правильный ответ, цветные — что нашла модель.
`python tools\make_toy_dataset.py --name toyids --ids 7,3,5` делает тот же набор с «перепутанными» id категорий —
так проверяется, что номера классов в labels.json совпадают с выходами модели.

Поучительная ловушка: при слишком коротком обучении (10 эпох на игрушке) mAP уже высокий, но у первого класса
уверенность ещё ниже 0,5 — в `eval.py` у него полнота 0 при AP50 около 0,8. mAP считается по всем порогам и этого
не видит; смотрите на точность и полноту при рабочем пороге.

## 3. Сбор кадров с телефона

1. Запустите HitCam для ПК и подключите телефон: пока телефон не передаёт видео, камера «HitCam» пустая.
2. Проверьте, что камера видна: `python collect.py --list`. В Windows 11 работающий HitCam виден как `msmf … HitCam`,
   в Windows 10 — как `dshow … HitCam`. Закройте программы, которые держат камеру (Zoom, OBS, браузер с видеозвонком).
3. Снимайте:

```powershell
python collect.py cups                   # окно с видео: Пробел — сохранить кадр, A — авто, Q — выход
python collect.py cups --every 2         # плюс автоматически раз в 2 секунды (если кадр заметно изменился)
python collect.py cups --camera "USB"    # другая камера по части имени; или --index 1 --backend dshow
```

Кадры ложатся в `data\cups\images\`. Сколько и каких кадров нужно — в разделе «Сколько данных» в
[docs/vision.md](../docs/vision.md#сколько-нужно-данных). Для первой попытки: 150–300 кадров, по 50+ рамок на класс,
разные ракурсы, расстояния, фоны и свет, 10–20 % кадров вообще без ваших объектов.

## 4. Разметка в Label Studio

[Label Studio](https://github.com/HumanSignal/label-studio) (Apache 2.0) работает локально в браузере, данные
никуда не уходят. Ставьте его в **отдельное** окружение: у него свои версии библиотек, которые конфликтуют с обучением.

```powershell
# вариант 1: отдельный venv вне репозитория
py -3.11 -m venv %USERPROFILE%\label-studio-venv
%USERPROFILE%\label-studio-venv\Scripts\pip install label-studio
%USERPROFILE%\label-studio-venv\Scripts\label-studio start
# вариант 2: pipx (pip install pipx), тогда просто
pipx install label-studio
label-studio start
```

Откроется http://localhost:8080 — зарегистрируйтесь (учётная запись локальная). Дальше:

1. **Create Project** → имя любое.
2. **Data Import** → перетащите файлы из `data\cups\images` (веб-загрузка берёт до 100 файлов за раз — грузите
   пачками).
3. **Labeling Setup** → **Computer Vision → Object Detection with Bounding Boxes**. Удалите примеры меток и впишите
   свои классы (имена коротко, можно по-русски: они же будут подписями в HitCam). В режиме **Code** это выглядит так:

   ```xml
   <View>
     <Image name="image" value="$image"/>
     <RectangleLabels name="label" toName="image">
       <Label value="кружка"/>
       <Label value="ключи"/>
     </RectangleLabels>
   </View>
   ```

4. Размечайте: клавиша `1`, `2`, … выбирает класс, мышью рисуется прямоугольник, **Submit** (`Ctrl+Enter`) —
   следующая картинка. Правила:
   - рамка плотно по видимой части объекта, без запаса;
   - размечайте **каждый** экземпляр класса на кадре, даже маленький или частично закрытый (от ~30 % видимости);
     неразмеченный объект модель выучит как «фон»;
   - кадр без ваших объектов тоже нажимайте **Submit** — это полезный пример «здесь ничего нет»;
   - кадры, где не понять, что это, лучше удалить, чем размечать наугад.
5. **Export** → **COCO** (в новых версиях есть ещё **COCO with images** — тоже подходит) → скачается zip. Распакуйте его в `data\cups\export\`, чтобы
   получилось `data\cups\export\result.json` (и, если выгрузились, `data\cups\export\images\`).

## 5. Деление на train / valid / test

```powershell
python split.py cups                        # 70 % train, 15 % valid, 15 % test
python split.py cups --valid 0.2 --test 0.1 --seed 3
```

- **train** — на этом модель учится;
- **valid** — по нему после каждой эпохи выбирается лучшая версия и решается, когда остановиться;
- **test** — модель его никогда не видит; честная оценка в `eval.py`.

`split.py` находит картинки из `result.json` (в `export\images` или в `data\cups\images`, в том числе без префикса
`1a2b3c4d-`, который Label Studio добавляет к загруженным файлам), перемешивает с фиксированным `--seed` и пишет
раскладку, которую ждёт RF-DETR:

```
data\cups\
    images\                      кадры из collect.py (не меняются)
    export\result.json           экспорт Label Studio (не меняется)
    train\_annotations.coco.json + картинки train
    valid\_annotations.coco.json + картинки valid
    test\_annotations.coco.json  + картинки test
```

В каждом `_annotations.coco.json` лежит полный список категорий; `file_name` — просто имя файла в той же папке.
RF-DETR сортирует категории по `id` и нумерует их 0, 1, 2, … — это номера столбцов на выходе модели и ключи
`classes` в labels.json. `split.py` делит случайно: если кадры — длинные серии почти одинаковых снимков, соседние
кадры попадут и в train, и в test, и оценка выйдет завышенной. Честнее снять кадры для test отдельно (другой день,
другое место) или хотя бы снимать короткими разнообразными сериями.

## 6. Обучение

```powershell
python train.py cups                        # RF-DETR Nano, до 50 эпох, остановка, если 10 эпох нет улучшения
python train.py cups --model small          # точнее, но в ~2 раза медленнее в HitCam
python train.py cups --epochs 100 --patience 15 --name cups-long
python train.py cups --resume runs\cups-nano\last.ckpt      # продолжить прерванное
```

Настройки по умолчанию рассчитаны на 8 ГБ видеопамяти: Nano — 8 картинок за шаг × 2 шага накопления (на RTX 3070
пик 4,6 ГБ), Small — 4 × 4 (пик 3,6 ГБ); в обоих случаях эффективный батч 16. Первые 20 % эпох не участвуют в
выборе лучшей версии (`--skip-best`): случайно высокий mAP в начале обычно идёт с низкой уверенностью. Не хватает памяти (`CUDA out of memory`) — уменьшите `--batch` и
во столько же раз увеличьте `--accum`, например `--batch 4 --accum 4`.

В `runs\cups-nano\` появятся `checkpoint_best_total.pth` (лучшие веса по valid), `metrics.csv` (потери и mAP по
эпохам, открывается в Excel), `training_config.json` (все настройки и имена классов). Если установить
`pip install tensorboard`, графики можно смотреть в `tensorboard --logdir runs`.

## 7. Оценка

```powershell
python eval.py cups-nano                    # на test
python eval.py cups-nano --threshold 0.4 --save 30
```

Печатает mAP@50, mAP@50:95 и таблицу по классам (AP50, точность, полнота, TP / FP / FN при пороге уверенности
`--threshold`), пишет `runs\cups-nano\eval\metrics.json` и картинки: зелёные рамки — правильный ответ, цветные с
числом — ответ модели. Как читать эти числа — в [docs/vision.md](../docs/vision.md#как-мерить-качество).

## 8. Экспорт и подключение к HitCam

```powershell
python export.py cups-nano --install
python export.py cups-nano --stem cups --title "Кружки и ключи" --install
```

Скрипт пишет `output\models\<stem>.onnx` (FP32; FP16 на DirectML у нас не работает) и `<stem>.labels.json`,
прогоняет обе версии — PyTorch и ONNX на DirectML — на кадрах test и требует совпадения (те же классы, IoU рамок
≥ 0,95). С `--install` копирует оба файла в `%LOCALAPPDATA%\HitCam\models\`; HitCam показывает модель в списке под
именем из `--title`.

`labels.json` для дообученной модели:

```json
{
  "name": "Кружки и ключи",
  "format": "rfdetr",
  "input": [384, 384],
  "mean": [0.485, 0.456, 0.406],
  "std": [0.229, 0.224, 0.225],
  "resize": "stretch",
  "outputs": {"boxes": "dets", "logits": "labels"},
  "classes": {"0": "кружка", "1": "ключи"},
  "license": "Apache-2.0"
}
```

`classes` — номер столбца выхода `labels` → имя. У дообученной модели столбцов на один больше, чем классов:
последний — служебный «не объект», у него нет имени. У стандартной COCO-модели 91 столбец, номер столбца равен
id категории COCO (1 = person, …, 90 = toothbrush).

Проверить любую модель на своих фото и сравнить DirectML с процессором:

```powershell
python check_onnx.py $env:LOCALAPPDATA\HitCam\models\cups.onnx data\cups\test --save output\check
```

## Стандартная модель (для сборки HitCam)

```powershell
python export_default.py                     # output\models\rfdetr-nano.onnx + rfdetr-nano.labels.json
python export_default.py --model small
```

Работает без видеокарты (так её запускает CI); для этого достаточно `requirements-export.txt` и torch с
`--index-url https://download.pytorch.org/whl/cpu`.

## Лицензии

RF-DETR и его веса — Apache 2.0 (Roboflow), значит и дообученные модели можно свободно распространять.
Label Studio Community — Apache 2.0, OpenCV — Apache 2.0, ONNX Runtime — MIT. Почему не Ultralytics YOLO — в
[docs/vision.md](../docs/vision.md#почему-rf-detr).
