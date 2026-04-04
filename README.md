# Webcam MCP

MCP-сервер для **ручного** захвата с камеры и микрофона и передачи данных агенту для анализа.
Поддерживает одиночный кадр, burst-серии и аудио-burst в WAV.

## Модульная раскладка

Тот же функционал в стеке **financial-open** разнесён по трём сабмодулям (источник правды — GitLab; на GitHub — зеркала):

- [webcam-mcp-shared](https://github.com/KarataevDmitry/webcam-mcp-shared) — общая библиотека  
- [webcam-capture-mcp](https://github.com/KarataevDmitry/webcam-capture-mcp) — захват (камера, экран, аудио, A/V)  
- [webcam-analysis-mcp](https://github.com/KarataevDmitry/webcam-analysis-mcp) — анализ, OCR, Whisper  

**Этот репозиторий** — один процесс со **всеми** тулами в одном MCP; если так проще подключать в `mcp.json`, это остаётся нормальным вариантом.

## Стек

- C#, .NET 10, win-x64, self-contained
- `ModelContextProtocol` (C# SDK)
- `OpenCvSharp4` + `OpenCvSharp4.runtime.win`
- `NAudio`
- `Whisper.net` + `Whisper.net.Runtime`

## Лицензия

MIT (см. `LICENSE`).

## Публикация

```bash
dotnet publish -c Release -o publish
```

Для подключения удобно использовать фиксированный путь к exe (например через junction), затем добавить сервер в `mcp.json`.

## Пример `mcp.json`

```json
{
  "mcpServers": {
    "webcam-mcp": {
      "command": "D:\\webcam-mcp\\WebcamMcp.exe",
      "args": []
    }
  }
}
```

## Tool

### `capture_webcam_frame`

Делает одиночный снимок с выбранной камеры и сохраняет изображение в workspace.

Обязательный параметр:

- `workspace_path` — путь к workspace.

Опциональные параметры:

- `camera_index` (int, default `0`)
- `width` / `height` (int)
- `warmup_frames` (int, default `5`)
- `image_format` (`jpg` | `png`, default `jpg`)
- `jpeg_quality` (int `1..100`, default `92`)
- `output_subdir` (relative path, default `.cascade-ide\\webcam-captures`)
- `file_name` (без расширения)

Ответ: JSON-строка с `file_path`, `width`, `height`, `camera_index`, `image_format`, `captured_at_utc`.

### `capture_webcam_burst`

Делает серию кадров с камеры в течение заданной длительности.

Обязательный параметр:

- `workspace_path` — путь к workspace.

Опциональные параметры:

- `camera_index` (int, default `0`)
- `width` / `height` (int)
- `warmup_frames` (int, default `5`)
- `duration_sec` (int, default `2`)
- `target_fps` (int, default `24`)
- `image_format` (`jpg` | `png`, default `jpg`)
- `jpeg_quality` (int `1..100`, default `92`)
- `output_subdir` (relative path, default `.cascade-ide\\webcam-captures`)
- `burst_name` (имя серии, опционально)
- `save_video` (bool, default `false`)
- `video_fps` (int, default `24`)
- `video_format` (`mp4` | `avi`, default `mp4`)

Ответ: JSON-строка с `burst_dir`, `frames_captured`, `target_fps`, `actual_fps`, `video_path` (если включено), и метаданными кадра.

### `capture_screen_burst`

Делает серию кадров с экрана в течение заданной длительности (вместо веб-камеры).

Обязательный параметр:

- `workspace_path` — путь к workspace.

Опциональные параметры:

- `monitor` (int 1-based слева направо **или** `all`; если указан и `x/y/width/height` не переданы, регион берётся по выбранному монитору; `all` = весь виртуальный экран)
- `x` / `y` (int, default: начало виртуального экрана)
- `width` / `height` (int, default: размер виртуального экрана)
- `duration_sec` (int, default `2`)
- `target_fps` (int, default `24`)
- `image_format` (`jpg` | `png`, default `jpg`)
- `jpeg_quality` (int `1..100`, default `92`)
- `output_subdir` (relative path, default `.cascade-ide\\screen-captures`)
- `burst_name` (имя серии, опционально)
- `save_video` (bool, default `false`)
- `video_fps` (int, default `24`)
- `video_format` (`mp4` | `avi`, default `mp4`)

Ответ: JSON-строка с `burst_dir`, `frames_captured`, `target_fps`, `actual_fps`, `capture_region`, `video_path` (если включено) и временем захвата.

### `analyze_burst_sequence`

Анализирует папку burst как последовательность кадров и возвращает структурный отчёт о динамике.

Обязательные параметры:

- `workspace_path` — путь к workspace.
- `burst_dir` — путь к папке burst (абсолютный или относительный к workspace).

Опциональные параметры:

- `sample_every` (int, default `1`) — анализировать каждый N-й кадр
- `max_frames` (int, default `3000`) — лимит кадров
- `scene_cut_threshold` (number `1..255`, default `35`) — порог «резкой смены сцены»

Ответ содержит:

- `avg_motion_score`, `min_motion_score`, `max_motion_score`
- `scene_cut_count`
- `top_motion_peaks`
- `timeline` (переходы между кадрами с оценкой движения)
- краткий `summary`

### `capture_audio_burst`

Записывает короткий WAV-фрагмент с микрофона по явной команде.

Обязательный параметр:

- `workspace_path` — путь к workspace.

Опциональные параметры:

- `duration_sec` (int, default `10`)
- `sample_rate` (int, default `16000`)
- `channels` (int, default `1`)
- `device_number` (int, default `0`)
- `output_subdir` (relative path, default `.cascade-ide\\audio-captures`)
- `file_name` (имя файла без расширения)

Ответ содержит путь к WAV и параметры записи.

### `analyze_audio_sequence`

Анализирует WAV-файл и возвращает таймлайн по окнам громкости.

Обязательные параметры:

- `workspace_path`
- `audio_path` (абсолютный или относительный к workspace)

Опциональные параметры:

- `frame_ms` (int, default `50`)
- `silence_threshold_db` (number, default `-45`)

Ответ содержит:

- `duration_sec`, `peak_dbfs`, `avg_rms`, `activity_ratio`, `silence_ratio`
- `zero_crossings_per_sec`
- `timeline` и краткий `summary`

### `transcribe_audio_whisper`

Локальная транскрипция аудио через Whisper.net (на базе whisper.cpp runtime).

Обязательные параметры:

- `workspace_path`
- `audio_path`

Опциональные параметры:

- `model_path` — путь к локальной модели Whisper (`ggml/gguf`)
- `language` — `auto` (по умолчанию), `ru`, `en`, ...
- `max_segments` — лимит сегментов в ответе

Если `model_path` не передан, используется переменная окружения:

- `WHISPER_MODEL_PATH`

Ответ содержит:

- `transcript` (полный текст)
- `segments` (таймкоды + текст)

### `capture_av_burst`

Снимает короткую синхронную A/V-сессию:

- кадры с камеры в `frames/`
- аудио в `audio.wav`
- метаданные тайминга в `metadata.json`
- опционально `video.mp4`

Обязательный параметр:

- `workspace_path`

Опциональные параметры:

- `duration_sec` (int, default `10`)
- `target_fps` (int, default `24`)
- `camera_index` (int, default `0`)
- `audio_device_number` (int, default `0`)
- `width` / `height` (int)
- `audio_sample_rate` (int, default `16000`)
- `audio_channels` (int, default `1`)
- `warmup_frames` (int, default `5`)
- `image_format` (`jpg` | `png`)
- `jpeg_quality` (int `1..100`)
- `output_subdir` (default `.cascade-ide\\av-captures`)
- `session_name` (опционально)
- `save_video` (bool, default `true`)
- `video_fps` (int, default `24`)

### `capture_screen_av_burst`

Одновременная запись короткой A/V-сессии: кадры с экрана + WAV с микрофона + метаданные синхронизации.

Обязательный параметр:

- `workspace_path` — путь к workspace.

Опциональные параметры:

- `duration_sec` (int, default `10`)
- `target_fps` (int, default `24`)
- `audio_device_number` (int, default `0`)
- `monitor` (int 1-based слева направо **или** `all`; если указан и `x/y/width/height` не переданы, регион берётся по выбранному монитору; `all` = весь виртуальный экран)
- `x` / `y` (int, default: начало виртуального экрана)
- `width` / `height` (int, default: размер виртуального экрана)
- `audio_sample_rate` (int, default `16000`)
- `audio_channels` (int, default `1`)
- `image_format` (`jpg` | `png`, default `jpg`)
- `jpeg_quality` (int `1..100`, default `92`)
- `output_subdir` (relative path, default `.cascade-ide\\av-captures`)
- `session_name` (имя сессии, опционально)
- `save_video` (bool, default `true`)
- `video_fps` (int, default `24`)

Ответ: JSON-строка с `session_dir`, `frames_dir`, `audio_path`, `video_path`, `metadata_path`, `frame_count`, `actual_fps`, `capture_region`.

### `analyze_av_sequence`

Комплексный отчёт по A/V-сессии: объединяет видео- и аудио-анализ.

Обязательные параметры:

- `workspace_path`
- `session_dir`

Опциональные параметры:

- `sample_every`, `max_frames`, `scene_cut_threshold` (для видео)
- `audio_frame_ms`, `silence_threshold_db` (для аудио)

Ответ содержит:

- `av_profile` (интегральный тип сцены)
- `summary`
- `video_analysis`
- `audio_analysis`

## Приватность и безопасность

- Съёмка выполняется **только** при явном вызове tool.
- Файлы сохраняются только внутри `workspace_path` (попытки выхода наружу блокируются).
