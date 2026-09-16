# Unity Video Bridge

Прототип программного моста для приёма WebRTC-видеопотока, отправленного из GStreamer, декодирования VP8 и отображения кадров внутри Unity как `Texture2D`.

Проект является частью работы по созданию дублирующей системы видеоконтроля хирургического комплекса da Vinci. На текущем этапе вместо реального видеосигнала da Vinci используется тестовый поток GStreamer: задача прототипа проверить всю цепочку от WebRTC-приёма до отображения видео в сцене Unity.

> Репозиторий основан на исходном проекте [`ua-i2cat/gst-unity-bridge`](https://github.com/ua-i2cat/gst-unity-bridge). В него добавлен отдельный WebRTC-путь для Unity 6 на Linux, собственный VP8 decoder bridge и диагностическая инфраструктура.

---

## Что реализовано

В проекте проверен полный путь:

```text
GStreamer / webrtcsink
        ↓
WebRTC + rs-signalling
        ↓
GstWebRtcReceiver.Core
        ↓
SIPSorcery RTCPeerConnection
        ↓
encoded VP8 frame
        ↓
Vp8DecoderBridge + libvpx
        ↓
I420
        ↓
RGBA
        ↓
Unity Texture2D
        ↓
WebRtcVideoQuad
```

В результате Unity получает живой WebRTC-видеопоток, декодирует VP8-кадры и отображает их на объекте сцены.

### Основные выполненные этапы

1. Исходный `gst-unity-bridge` был адаптирован и проверен в Unity 6 на Ubuntu.
2. Проверен базовый путь отображения видео на `Quad` через Unity texture.
3. Отдельно проверено обновление `Texture2D` из C# без WebRTC, чтобы исключить ошибки рендеринга Unity.
4. C# WebRTC receiver из проекта `gst-playground` был выделен в библиотеку `GstWebRtcReceiver.Core` и подключён к Unity.
5. В Unity подтверждены signalling, SDP, ICE, DTLS/SRTP и приём RTP-пакетов.
6. По SDP и RTP определён текущий видеокодек: VP8, payload type 96.
7. Выяснено, что `SIPSorcery.OnVideoFrameReceived` в используемом receive path отдаёт собранный **encoded frame**, а не готовые пиксели.
8. Реализован нативный Linux-плагин `Vp8DecoderBridge`, использующий системный `libvpx`.
9. Декодированный I420-кадр преобразуется в RGBA.
10. RGBA-буфер передаётся на главный поток Unity, загружается в `Texture2D` через `LoadRawTextureData()` и применяется через `Apply()`.
11. Текстура назначается объекту `WebRtcVideoQuad`.
12. Добавлены диагностические маркеры, позволяющие отследить прохождение каждого кадра по всей цепочке.

---

## Архитектура проекта

### Сторона отправителя

Отправитель находится во внешнем проекте `gst-playground` и использует GStreamer `webrtcsink`.

```text
video source
   ↓
GStreamer
   ↓
VP8 encoding
   ↓
webrtcsink
   ↓
WebRTC peer connection
```

`rs-signalling` используется для установления WebRTC-сессии. Он передаёт служебные signalling-сообщения, SDP и информацию для установления соединения; само видео после установления соединения передаётся как WebRTC/RTP media.

### Сторона Unity

```text
UnityWebRtcSmokeReceiver
        ↓
GstWebRtcReceiver.Core
        ↓
SIPSorcery
        ↓
VP8 encoded frame
        ↓
Vp8DecoderBridge.cs
        ↓ P/Invoke
libVp8DecoderBridge.so
        ↓
libvpx
        ↓
I420 → RGBA
        ↓
Texture2D (RGBA32)
        ↓
Renderer / WebRtcVideoQuad
```

### Важное отличие от старого `gst-unity-bridge`

В репозитории фактически существуют два разных пути отображения видео.

#### 1. Legacy GUB path

Исходный `gst-unity-bridge` использует native GStreamer plugin:

```text
media URI
  ↓
GStreamer inside native GUB plugin
  ↓
decoded frame
  ↓
Unity Texture
```

За него отвечают:

- `Plugin/GUB/`
- `Unity/Assets/GstUnityBridge/Scripts/GstUnityBridge/`
- старые sample scenes `Local-clip`, `Remote-clip` и др.

#### 2. Новый WebRTC path

Финальный прототип этой работы не использует GStreamer playback внутри Unity:

```text
external GStreamer/webrtcsink
  ↓ WebRTC
C# receiver inside Unity
  ↓
VP8 decoder bridge
  ↓
Texture2D
```

Этот путь реализован в `Scripts/WebRtc` и сцене `WebRtcReceiverSmokeTest`.

---


## Основные компоненты

| Компонент | Назначение |
|---|---|
| GStreamer | Создание и обработка исходного видеопотока |
| `webrtcsink` | Отправка видеопотока через WebRTC |
| `rs-signalling` | Signalling для установления WebRTC-сессии |
| `GstWebRtcReceiver.Core` | C# receiver: signalling, SDP, ICE, DTLS/SRTP, RTP |
| SIPSorcery | Реализация WebRTC/RTCPeerConnection на стороне C# |
| VP8 | Текущий видеокодек передаваемого потока |
| `Vp8DecoderBridge` | Нативный мост между C# и `libvpx` |
| `libvpx` | Декодирование VP8 |
| I420 | Формат декодированного YUV-кадра из `libvpx` |
| RGBA | Формат пикселей, подготовленный для Unity |
| `Texture2D` | Unity-объект, в который загружается очередной видеокадр |
| `WebRtcVideoQuad` | Объект сцены, на котором отображается видео |

---

## Требования

Текущий WebRTC-прототип проверялся со следующей конфигурацией:

- Ubuntu / Linux x86_64
- Unity **6000.3.17f1 (Unity 6.3 LTS)**
- внешний `gst-playground` с рабочими:
  - `rs-signalling`
  - GStreamer producer с `webrtcsink`
- signalling URL по умолчанию: `ws://localhost:8443`
- VP8 video stream
- `libvpx.so.9` или совместимый `libvpx.so`
- `gcc` и `make` для пересборки native decoder bridge


## Запуск WebRTC-видео в Unity

### 1. Запустить внешнюю WebRTC-инфраструктуру

В `gst-playground` должны быть запущены:

- signalling server;
- GStreamer producer с `webrtcsink`;
- VP8-видеопоток.

Команды запуска зависят от конфигурации внешнего `gst-playground` и в этом репозитории не хранятся.


### 2. Открыть Unity-проект


### 3. Открыть сцену

```text
Assets/GstUnityBridge/Test Scenes/Experimental/WebRtcReceiverSmokeTest.unity
```

### 4. Проверить объект `WebRtcReceiver`

Основные параметры `UnityWebRtcSmokeReceiver`:

- `Signalling Url`: `ws://localhost:8443`
- `Room Id`: оставить пустым, если room routing не используется
- `Timeout Seconds`: например `45`
- `Target Quad Name`: `WebRtcVideoQuad`

### 5. Нажать Play

При успешной работе в Console последовательно появляются сообщения о:

- подключении к signalling;
- SDP negotiation;
- ICE state;
- DTLS state;
- RTP packets;
- negotiated codec;
- encoded VP8 frames;
- VP8 decode;
- RGBA copy;
- обновлении `Texture2D`.

В Game View видеопоток должен отображаться на объекте `WebRtcVideoQuad`.

---

## Основные проблемы, которые были решены

### Несовместимость .NET assembly с Unity

Первоначальный C# receiver собирался под `net8.0`, тогда как Unity не мог использовать эту сборку напрямую.

Решение: receiver core был подготовлен в совместимом варианте для Unity (`netstandard2.1`) и подключён вместе с зависимостями как managed plugin.

### `OnVideoFrameReceived` не даёт готовые пиксели

Callback содержит encoded video frame.

Решение: добавить отдельный decoder stage.

### Нет готового managed VP8 decoder

Решение: создан минимальный native bridge поверх системного `libvpx`.

### Неправильное чтение размеров кадра

При первой реализации структура `vpx_image_t` была описана неполно.

Решение: исправлен layout, включая поле `bit_depth`, после чего корректно читаются `d_w` и `d_h`.

### `EntryPointNotFoundException: vp8_decoder_copy_rgba`

Unity загружал старую версию `.so`, в которой не было нового экспорта.

Решение:

- пересобрать native plugin;
- заменить библиотеку в `Assets/Plugins`;
- проверить экспорт через `nm -D`;
- добавить runtime probe через `dlopen/dlsym`.


## Текущие ограничения

- WebRTC video decoder path поддерживает VP8.
- Native `Vp8DecoderBridge` сейчас ориентирован на Linux x86_64.
- Используется системный `libvpx`.
- Реальный видеосигнал da Vinci пока не подключён; используется тестовый GStreamer producer.
- Измерение end-to-end latency оставлено как отдельный следующий этап.
- Исходники `GstWebRtcReceiver.Core` находятся во внешнем `gst-playground`; здесь лежит его собранный Unity-compatible DLL.

---
