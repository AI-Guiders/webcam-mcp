using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenCvSharp;
using Whisper.net;
using Tool = ModelContextProtocol.Protocol.Tool;

static JsonElement Schema(object schema) => JsonSerializer.SerializeToElement(schema);

const string DefaultOutputSubdir = @".cascade-ide\webcam-captures";
const int DefaultWarmupFrames = 5;
const int DefaultJpegQuality = 92;
const int DefaultBurstDurationSec = 2;
const int DefaultBurstTargetFps = 24;
const int DefaultBurstVideoFps = 24;
const string DefaultScreenOutputSubdir = @".cascade-ide\screen-captures";
const string DefaultAudioOutputSubdir = @".cascade-ide\audio-captures";
const int DefaultAudioDurationSec = 10;
const int DefaultAudioSampleRate = 16000;
const int DefaultAudioChannels = 1;
const int DefaultAudioFrameMs = 50;
const double DefaultAudioSilenceDb = -45;
const string EnvWhisperModelPath = "WHISPER_MODEL_PATH";
const string DefaultAvOutputSubdir = @".cascade-ide\av-captures";

const int SmXVirtualScreen = 76;
const int SmYVirtualScreen = 77;
const int SmCxVirtualScreen = 78;
const int SmCyVirtualScreen = 79;

[DllImport("user32.dll")]
static extern int GetSystemMetrics(int nIndex);

[DllImport("user32.dll")]
static extern bool EnumDisplayMonitors(
    IntPtr hdc,
    IntPtr lprcClip,
    MonitorEnumProc lpfnEnum,
    IntPtr dwData);

var toolsList = new List<Tool>
{
    new()
    {
        Name = "capture_webcam_frame",
        Description = "Сделать снимок с веб-камеры по явному запросу. Сохраняет изображение в workspace (по умолчанию .cascade-ide/webcam-captures) и возвращает JSON с путём и параметрами кадра.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                camera_index = new { type = "integer", description = "Индекс камеры (по умолчанию 0)." },
                width = new { type = "integer", description = "Желаемая ширина кадра (опционально)." },
                height = new { type = "integer", description = "Желаемая высота кадра (опционально)." },
                warmup_frames = new { type = "integer", description = "Количество прогревочных кадров перед снимком (по умолчанию 5)." },
                image_format = new { type = "string", description = "Формат: jpg или png (по умолчанию jpg)." },
                jpeg_quality = new { type = "integer", description = "Качество JPEG 1..100 (по умолчанию 92)." },
                output_subdir = new { type = "string", description = @"Подкаталог внутри workspace для сохранения кадров (по умолчанию .cascade-ide\webcam-captures)." },
                file_name = new { type = "string", description = "Имя файла без расширения (опционально)." }
            },
            required = new[] { "workspace_path" }
        })
    },
    new()
    {
        Name = "capture_webcam_burst",
        Description = "Сделать быструю серию кадров с веб-камеры. Сохраняет кадры в подпапку внутри workspace и (опционально) собирает короткий видеофайл.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                camera_index = new { type = "integer", description = "Индекс камеры (по умолчанию 0)." },
                width = new { type = "integer", description = "Желаемая ширина кадра (опционально)." },
                height = new { type = "integer", description = "Желаемая высота кадра (опционально)." },
                warmup_frames = new { type = "integer", description = "Количество прогревочных кадров перед серией (по умолчанию 5)." },
                duration_sec = new { type = "integer", description = "Длительность серии в секундах (по умолчанию 2)." },
                target_fps = new { type = "integer", description = "Целевой FPS съёмки (по умолчанию 24)." },
                image_format = new { type = "string", description = "Формат кадров: jpg или png (по умолчанию jpg)." },
                jpeg_quality = new { type = "integer", description = "Качество JPEG 1..100 (по умолчанию 92)." },
                output_subdir = new { type = "string", description = @"Подкаталог внутри workspace для сохранения серии (по умолчанию .cascade-ide\webcam-captures)." },
                burst_name = new { type = "string", description = "Имя серии (опционально)." },
                save_video = new { type = "boolean", description = "Сохранить также видеофайл (по умолчанию false)." },
                video_fps = new { type = "integer", description = "FPS для сохранённого видео (по умолчанию 24)." },
                video_format = new { type = "string", description = "Формат видео: mp4 или avi (по умолчанию mp4)." }
            },
            required = new[] { "workspace_path" }
        })
    },
    new()
    {
        Name = "capture_screen_burst",
        Description = "Сделать быструю серию кадров с экрана. Сохраняет кадры в подпапку внутри workspace и (опционально) собирает короткий видеофайл.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                monitor = new
                {
                    description = "Монитор: номер (1-based слева направо) или 'all'. Если указан и x/y/width/height не заданы, регион берётся автоматически.",
                    anyOf = new object[]
                    {
                        new { type = "integer" },
                        new { type = "string" }
                    }
                },
                x = new { type = "integer", description = "Левая координата области захвата (по умолчанию виртуальный экран)." },
                y = new { type = "integer", description = "Верхняя координата области захвата (по умолчанию виртуальный экран)." },
                width = new { type = "integer", description = "Ширина области захвата (по умолчанию ширина виртуального экрана)." },
                height = new { type = "integer", description = "Высота области захвата (по умолчанию высота виртуального экрана)." },
                duration_sec = new { type = "integer", description = "Длительность серии в секундах (по умолчанию 2)." },
                target_fps = new { type = "integer", description = "Целевой FPS съёмки (по умолчанию 24)." },
                image_format = new { type = "string", description = "Формат кадров: jpg или png (по умолчанию jpg)." },
                jpeg_quality = new { type = "integer", description = "Качество JPEG 1..100 (по умолчанию 92)." },
                output_subdir = new { type = "string", description = @"Подкаталог внутри workspace для сохранения серии (по умолчанию .cascade-ide\screen-captures)." },
                burst_name = new { type = "string", description = "Имя серии (опционально)." },
                save_video = new { type = "boolean", description = "Сохранить также видеофайл (по умолчанию false)." },
                video_fps = new { type = "integer", description = "FPS для сохранённого видео (по умолчанию 24)." },
                video_format = new { type = "string", description = "Формат видео: mp4 или avi (по умолчанию mp4)." }
            },
            required = new[] { "workspace_path" }
        })
    },
    new()
    {
        Name = "analyze_burst_sequence",
        Description = "Проанализировать серию кадров (burst) как квазии-видео: оценить динамику движения, выделить пики/сцены и вернуть структурированный отчёт.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                burst_dir = new { type = "string", description = "Путь к папке burst (абсолютный или относительный к workspace)." },
                sample_every = new { type = "integer", description = "Брать каждый N-й кадр для анализа (по умолчанию 1)." },
                max_frames = new { type = "integer", description = "Максимум кадров для анализа (по умолчанию 3000)." },
                scene_cut_threshold = new { type = "number", description = "Порог резкой смены сцены по шкале 0..255 (по умолчанию 35)." }
            },
            required = new[] { "workspace_path", "burst_dir" }
        })
    },
    new()
    {
        Name = "capture_audio_burst",
        Description = "Записать короткий аудиофрагмент с микрофона в WAV по явной команде. Сохраняет файл в workspace (по умолчанию .cascade-ide/audio-captures).",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                duration_sec = new { type = "integer", description = "Длительность записи в секундах (по умолчанию 10)." },
                sample_rate = new { type = "integer", description = "Частота дискретизации (по умолчанию 16000)." },
                channels = new { type = "integer", description = "Число каналов (по умолчанию 1)." },
                device_number = new { type = "integer", description = "Номер устройства микрофона (по умолчанию 0)." },
                output_subdir = new { type = "string", description = @"Подкаталог внутри workspace (по умолчанию .cascade-ide\audio-captures)." },
                file_name = new { type = "string", description = "Имя wav-файла без расширения (опционально)." }
            },
            required = new[] { "workspace_path" }
        })
    },
    new()
    {
        Name = "analyze_audio_sequence",
        Description = "Проанализировать WAV-файл: громкость, пики, долю тишины, активность речи/звука и таймлайн по окнам.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                audio_path = new { type = "string", description = "Путь к WAV-файлу (абсолютный или относительный к workspace)." },
                frame_ms = new { type = "integer", description = "Размер окна анализа в мс (по умолчанию 50)." },
                silence_threshold_db = new { type = "number", description = "Порог тишины в dBFS (по умолчанию -45)." }
            },
            required = new[] { "workspace_path", "audio_path" }
        })
    },
    new()
    {
        Name = "transcribe_audio_whisper",
        Description = "Локальная транскрипция WAV-аудио через Whisper.net (whisper.cpp runtime). Возвращает распознанный текст и сегменты с таймкодами.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                audio_path = new { type = "string", description = "Путь к аудиофайлу (абсолютный или относительный к workspace)." },
                model_path = new { type = "string", description = "Путь к ggml/gguf-модели Whisper. Если не задан — берётся из переменной окружения WHISPER_MODEL_PATH." },
                language = new { type = "string", description = "Язык (например ru, en, auto). По умолчанию auto." },
                max_segments = new { type = "integer", description = "Ограничение количества сегментов в ответе (по умолчанию 1000)." }
            },
            required = new[] { "workspace_path", "audio_path" }
        })
    },
    new()
    {
        Name = "capture_av_burst",
        Description = "Одновременная запись короткой A/V-сессии: кадры с веб-камеры + WAV с микрофона + метаданные синхронизации.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                duration_sec = new { type = "integer", description = "Длительность захвата в секундах (по умолчанию 10)." },
                target_fps = new { type = "integer", description = "Целевой FPS для кадров (по умолчанию 24)." },
                camera_index = new { type = "integer", description = "Индекс камеры (по умолчанию 0)." },
                audio_device_number = new { type = "integer", description = "Индекс микрофона (по умолчанию 0)." },
                width = new { type = "integer", description = "Желаемая ширина кадра (опционально)." },
                height = new { type = "integer", description = "Желаемая высота кадра (опционально)." },
                audio_sample_rate = new { type = "integer", description = "Частота аудио (по умолчанию 16000)." },
                audio_channels = new { type = "integer", description = "Каналы аудио (по умолчанию 1)." },
                warmup_frames = new { type = "integer", description = "Прогрев кадров перед записью (по умолчанию 5)." },
                image_format = new { type = "string", description = "Формат кадров: jpg|png (по умолчанию jpg)." },
                jpeg_quality = new { type = "integer", description = "Качество JPEG 1..100 (по умолчанию 92)." },
                output_subdir = new { type = "string", description = @"Подкаталог внутри workspace (по умолчанию .cascade-ide\av-captures)." },
                session_name = new { type = "string", description = "Имя A/V-сессии (опционально)." },
                save_video = new { type = "boolean", description = "Собрать mp4 из кадров (по умолчанию true)." },
                video_fps = new { type = "integer", description = "FPS для mp4 (по умолчанию 24)." }
            },
            required = new[] { "workspace_path" }
        })
    },
    new()
    {
        Name = "capture_screen_av_burst",
        Description = "Одновременная запись короткой A/V-сессии: кадры с экрана + WAV с микрофона + метаданные синхронизации.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                duration_sec = new { type = "integer", description = "Длительность захвата в секундах (по умолчанию 10)." },
                target_fps = new { type = "integer", description = "Целевой FPS для кадров (по умолчанию 24)." },
                audio_device_number = new { type = "integer", description = "Индекс микрофона (по умолчанию 0)." },
                monitor = new
                {
                    description = "Монитор: номер (1-based слева направо) или 'all'. Если указан и x/y/width/height не заданы, регион берётся автоматически.",
                    anyOf = new object[]
                    {
                        new { type = "integer" },
                        new { type = "string" }
                    }
                },
                x = new { type = "integer", description = "Левая координата области экрана (по умолчанию виртуальный экран)." },
                y = new { type = "integer", description = "Верхняя координата области экрана (по умолчанию виртуальный экран)." },
                width = new { type = "integer", description = "Ширина области экрана (по умолчанию виртуальный экран)." },
                height = new { type = "integer", description = "Высота области экрана (по умолчанию виртуальный экран)." },
                audio_sample_rate = new { type = "integer", description = "Частота аудио (по умолчанию 16000)." },
                audio_channels = new { type = "integer", description = "Каналы аудио (по умолчанию 1)." },
                image_format = new { type = "string", description = "Формат кадров: jpg|png (по умолчанию jpg)." },
                jpeg_quality = new { type = "integer", description = "Качество JPEG 1..100 (по умолчанию 92)." },
                output_subdir = new { type = "string", description = @"Подкаталог внутри workspace (по умолчанию .cascade-ide\av-captures)." },
                session_name = new { type = "string", description = "Имя A/V-сессии (опционально)." },
                save_video = new { type = "boolean", description = "Собрать mp4 из кадров (по умолчанию true)." },
                video_fps = new { type = "integer", description = "FPS для mp4 (по умолчанию 24)." }
            },
            required = new[] { "workspace_path" }
        })
    },
    new()
    {
        Name = "analyze_av_sequence",
        Description = "Комплексный анализ A/V-сессии: объединяет динамику движения из кадров и аудио-активность в одном отчёте.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                session_dir = new { type = "string", description = "Путь к директории A/V-сессии (абсолютный или относительный к workspace)." },
                sample_every = new { type = "integer", description = "Видео-анализ: каждый N-й кадр (по умолчанию 1)." },
                max_frames = new { type = "integer", description = "Видео-анализ: максимум кадров (по умолчанию 3000)." },
                scene_cut_threshold = new { type = "number", description = "Видео-анализ: порог scene cut (по умолчанию 35)." },
                audio_frame_ms = new { type = "integer", description = "Аудио-анализ: окно в мс (по умолчанию 50)." },
                silence_threshold_db = new { type = "number", description = "Аудио-анализ: порог тишины dBFS (по умолчанию -45)." }
            },
            required = new[] { "workspace_path", "session_dir" }
        })
    }
};

static string HandleCaptureWebcamFrame(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var cameraIndex = GetOptionalInt(args, "camera_index", 0);
    var warmupFrames = Math.Clamp(GetOptionalInt(args, "warmup_frames", DefaultWarmupFrames), 0, 50);
    var requestedWidth = GetOptionalInt(args, "width", 0);
    var requestedHeight = GetOptionalInt(args, "height", 0);
    var jpegQuality = Math.Clamp(GetOptionalInt(args, "jpeg_quality", DefaultJpegQuality), 1, 100);
    var imageFormat = NormalizeImageFormat(GetOptionalString(args, "image_format") ?? "jpg");
    var outputSubdir = GetOptionalString(args, "output_subdir") ?? DefaultOutputSubdir;
    var fileName = GetOptionalString(args, "file_name");

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    if (Path.IsPathRooted(outputSubdir))
    {
        throw new ArgumentException("output_subdir must be relative to workspace_path.");
    }

    var outputDir = Path.GetFullPath(Path.Combine(workspaceRoot, outputSubdir));
    if (!outputDir.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("output_subdir points outside of workspace_path.");
    }

    Directory.CreateDirectory(outputDir);

    var safeBaseName = string.IsNullOrWhiteSpace(fileName)
        ? $"webcam-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}"
        : MakeSafeFileName(fileName);
    var outputPath = Path.Combine(outputDir, $"{safeBaseName}.{imageFormat}");

    using var capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.ANY);
    if (!capture.IsOpened())
    {
        throw new ArgumentException($"Camera {cameraIndex} is not available.");
    }

    if (requestedWidth > 0)
    {
        capture.Set(VideoCaptureProperties.FrameWidth, requestedWidth);
    }

    if (requestedHeight > 0)
    {
        capture.Set(VideoCaptureProperties.FrameHeight, requestedHeight);
    }

    using var frame = new Mat();

    for (var i = 0; i < warmupFrames; i++)
    {
        capture.Read(frame);
        Thread.Sleep(40);
    }

    if (!capture.Read(frame) || frame.Empty())
    {
        throw new ArgumentException("Failed to read frame from webcam.");
    }

    var writeOk = imageFormat switch
    {
        "jpg" => Cv2.ImWrite(outputPath, frame, [new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality)]),
        "png" => Cv2.ImWrite(outputPath, frame),
        _ => false
    };

    if (!writeOk)
    {
        throw new ArgumentException("Failed to save captured frame.");
    }

    var result = new
    {
        success = true,
        file_path = outputPath,
        width = frame.Width,
        height = frame.Height,
        camera_index = cameraIndex,
        image_format = imageFormat,
        captured_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

static string HandleCaptureWebcamBurst(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var cameraIndex = GetOptionalInt(args, "camera_index", 0);
    var warmupFrames = Math.Clamp(GetOptionalInt(args, "warmup_frames", DefaultWarmupFrames), 0, 50);
    var requestedWidth = GetOptionalInt(args, "width", 0);
    var requestedHeight = GetOptionalInt(args, "height", 0);
    var durationSec = Math.Clamp(GetOptionalInt(args, "duration_sec", DefaultBurstDurationSec), 1, 60);
    var targetFps = Math.Clamp(GetOptionalInt(args, "target_fps", DefaultBurstTargetFps), 1, 240);
    var jpegQuality = Math.Clamp(GetOptionalInt(args, "jpeg_quality", DefaultJpegQuality), 1, 100);
    var imageFormat = NormalizeImageFormat(GetOptionalString(args, "image_format") ?? "jpg");
    var outputSubdir = GetOptionalString(args, "output_subdir") ?? DefaultOutputSubdir;
    var burstName = GetOptionalString(args, "burst_name");
    var saveVideo = GetOptionalBool(args, "save_video", false);
    var videoFps = Math.Clamp(GetOptionalInt(args, "video_fps", DefaultBurstVideoFps), 1, 240);
    var videoFormat = NormalizeVideoFormat(GetOptionalString(args, "video_format") ?? "mp4");

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    if (Path.IsPathRooted(outputSubdir))
    {
        throw new ArgumentException("output_subdir must be relative to workspace_path.");
    }

    var outputDir = Path.GetFullPath(Path.Combine(workspaceRoot, outputSubdir));
    if (!outputDir.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("output_subdir points outside of workspace_path.");
    }

    Directory.CreateDirectory(outputDir);

    var safeBurstName = string.IsNullOrWhiteSpace(burstName)
        ? $"burst-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}"
        : MakeSafeFileName(burstName);
    var burstDir = Path.Combine(outputDir, safeBurstName);
    Directory.CreateDirectory(burstDir);

    using var capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.ANY);
    if (!capture.IsOpened())
    {
        throw new ArgumentException($"Camera {cameraIndex} is not available.");
    }

    if (requestedWidth > 0)
    {
        capture.Set(VideoCaptureProperties.FrameWidth, requestedWidth);
    }

    if (requestedHeight > 0)
    {
        capture.Set(VideoCaptureProperties.FrameHeight, requestedHeight);
    }

    using var frame = new Mat();

    for (var i = 0; i < warmupFrames; i++)
    {
        capture.Read(frame);
        Thread.Sleep(20);
    }

    var intervalMs = 1000.0 / targetFps;
    var durationMs = durationSec * 1000.0;
    var stopwatch = Stopwatch.StartNew();
    var nextCaptureAt = 0.0;
    var frameCount = 0;
    var firstFrameAtMs = -1.0;
    var lastFrameAtMs = -1.0;
    string? videoPath = null;
    VideoWriter? writer = null;

    try
    {
        while (stopwatch.Elapsed.TotalMilliseconds <= durationMs)
        {
            var elapsed = stopwatch.Elapsed.TotalMilliseconds;
            if (elapsed < nextCaptureAt)
            {
                var wait = Math.Max(1, (int)(nextCaptureAt - elapsed));
                Thread.Sleep(Math.Min(wait, 5));
                continue;
            }

            if (!capture.Read(frame) || frame.Empty())
            {
                nextCaptureAt += intervalMs;
                continue;
            }

            frameCount++;
            firstFrameAtMs = firstFrameAtMs < 0 ? elapsed : firstFrameAtMs;
            lastFrameAtMs = elapsed;

            var framePath = Path.Combine(burstDir, $"{frameCount:D5}.{imageFormat}");
            var saved = imageFormat switch
            {
                "jpg" => Cv2.ImWrite(framePath, frame, [new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality)]),
                "png" => Cv2.ImWrite(framePath, frame),
                _ => false
            };

            if (!saved)
            {
                throw new ArgumentException($"Failed to save burst frame: {framePath}");
            }

            if (saveVideo)
            {
                if (writer is null)
                {
                    videoPath = Path.Combine(burstDir, $"{safeBurstName}.{videoFormat}");
                    var fourcc = videoFormat == "avi"
                        ? VideoWriter.FourCC('M', 'J', 'P', 'G')
                        : VideoWriter.FourCC('m', 'p', '4', 'v');

                    writer = new VideoWriter(videoPath, fourcc, videoFps, new Size(frame.Width, frame.Height));
                    if (!writer.IsOpened())
                    {
                        writer.Dispose();
                        writer = null;
                        throw new ArgumentException("Failed to initialize video writer. Try video_format='avi' or another resolution.");
                    }
                }

                writer.Write(frame);
            }

            nextCaptureAt += intervalMs;
        }
    }
    finally
    {
        writer?.Release();
        writer?.Dispose();
    }

    if (frameCount == 0)
    {
        throw new ArgumentException("No frames were captured from webcam.");
    }

    var actualDurationMs = Math.Max(1.0, lastFrameAtMs - firstFrameAtMs);
    var actualFps = frameCount == 1 ? 1.0 : ((frameCount - 1) * 1000.0 / actualDurationMs);

    var result = new
    {
        success = true,
        burst_dir = burstDir,
        frames_captured = frameCount,
        target_fps = targetFps,
        actual_fps = Math.Round(actualFps, 2),
        duration_sec = durationSec,
        frame_width = frame.Width,
        frame_height = frame.Height,
        camera_index = cameraIndex,
        image_format = imageFormat,
        video_path = videoPath,
        captured_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

static string HandleCaptureScreenBurst(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var durationSec = Math.Clamp(GetOptionalInt(args, "duration_sec", DefaultBurstDurationSec), 1, 60);
    var targetFps = Math.Clamp(GetOptionalInt(args, "target_fps", DefaultBurstTargetFps), 1, 240);
    var jpegQuality = Math.Clamp(GetOptionalInt(args, "jpeg_quality", DefaultJpegQuality), 1, 100);
    var imageFormat = NormalizeImageFormat(GetOptionalString(args, "image_format") ?? "jpg");
    var outputSubdir = GetOptionalString(args, "output_subdir") ?? DefaultScreenOutputSubdir;
    var burstName = GetOptionalString(args, "burst_name");
    var saveVideo = GetOptionalBool(args, "save_video", false);
    var videoFps = Math.Clamp(GetOptionalInt(args, "video_fps", DefaultBurstVideoFps), 1, 240);
    var videoFormat = NormalizeVideoFormat(GetOptionalString(args, "video_format") ?? "mp4");

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    if (Path.IsPathRooted(outputSubdir))
    {
        throw new ArgumentException("output_subdir must be relative to workspace_path.");
    }

    var outputDir = Path.GetFullPath(Path.Combine(workspaceRoot, outputSubdir));
    if (!outputDir.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("output_subdir points outside of workspace_path.");
    }

    Directory.CreateDirectory(outputDir);

    var safeBurstName = string.IsNullOrWhiteSpace(burstName)
        ? $"screen-burst-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}"
        : MakeSafeFileName(burstName);
    var burstDir = Path.Combine(outputDir, safeBurstName);
    Directory.CreateDirectory(burstDir);

    var monitorNumber = GetOptionalMonitorNumber(args, "monitor");
    var hasExplicitRegion = args.ContainsKey("x") || args.ContainsKey("y") || args.ContainsKey("width") || args.ContainsKey("height");
    var region = ResolveCaptureRegion(monitorNumber, hasExplicitRegion);

    var captureX = GetOptionalInt(args, "x", region.X);
    var captureY = GetOptionalInt(args, "y", region.Y);
    var captureWidth = Math.Max(1, GetOptionalInt(args, "width", region.Width));
    var captureHeight = Math.Max(1, GetOptionalInt(args, "height", region.Height));

    var intervalMs = 1000.0 / targetFps;
    var durationMs = durationSec * 1000.0;
    var stopwatch = Stopwatch.StartNew();
    var nextCaptureAt = 0.0;
    var frameCount = 0;
    var firstFrameAtMs = -1.0;
    var lastFrameAtMs = -1.0;
    string? videoPath = null;
    VideoWriter? writer = null;

    try
    {
        while (stopwatch.Elapsed.TotalMilliseconds <= durationMs)
        {
            var elapsed = stopwatch.Elapsed.TotalMilliseconds;
            if (elapsed < nextCaptureAt)
            {
                var wait = Math.Max(1, (int)(nextCaptureAt - elapsed));
                Thread.Sleep(Math.Min(wait, 5));
                continue;
            }

            using var bitmap = new System.Drawing.Bitmap(
                captureWidth,
                captureHeight,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    captureX,
                    captureY,
                    0,
                    0,
                    new System.Drawing.Size(captureWidth, captureHeight),
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }

            frameCount++;
            firstFrameAtMs = firstFrameAtMs < 0 ? elapsed : firstFrameAtMs;
            lastFrameAtMs = elapsed;

            var framePath = Path.Combine(burstDir, $"{frameCount:D5}.{imageFormat}");
            SaveBitmapToPath(bitmap, framePath, imageFormat, jpegQuality);

            if (saveVideo)
            {
                using var frameMat = Cv2.ImRead(framePath, ImreadModes.Color);
                if (frameMat.Empty())
                {
                    throw new ArgumentException($"Failed to read saved frame for video: {framePath}");
                }
                if (writer is null)
                {
                    videoPath = Path.Combine(burstDir, $"{safeBurstName}.{videoFormat}");
                    var fourcc = videoFormat == "avi"
                        ? VideoWriter.FourCC('M', 'J', 'P', 'G')
                        : VideoWriter.FourCC('m', 'p', '4', 'v');

                    writer = new VideoWriter(videoPath, fourcc, videoFps, new OpenCvSharp.Size(frameMat.Width, frameMat.Height));
                    if (!writer.IsOpened())
                    {
                        writer.Dispose();
                        writer = null;
                        throw new ArgumentException("Failed to initialize video writer. Try video_format='avi' or another screen size.");
                    }
                }

                writer.Write(frameMat);
            }

            nextCaptureAt += intervalMs;
        }
    }
    finally
    {
        writer?.Release();
        writer?.Dispose();
    }

    if (frameCount == 0)
    {
        throw new ArgumentException("No frames were captured from screen.");
    }

    var actualDurationMs = Math.Max(1.0, lastFrameAtMs - firstFrameAtMs);
    var actualFps = frameCount == 1 ? 1.0 : ((frameCount - 1) * 1000.0 / actualDurationMs);

    var result = new
    {
        success = true,
        burst_dir = burstDir,
        frames_captured = frameCount,
        target_fps = targetFps,
        actual_fps = Math.Round(actualFps, 2),
        duration_sec = durationSec,
        capture_region = new { x = captureX, y = captureY, width = captureWidth, height = captureHeight },
        image_format = imageFormat,
        video_path = videoPath,
        captured_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

static string HandleAnalyzeBurstSequence(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var burstDirInput = GetRequiredString(args, "burst_dir");
    var sampleEvery = Math.Clamp(GetOptionalInt(args, "sample_every", 1), 1, 120);
    var maxFrames = Math.Clamp(GetOptionalInt(args, "max_frames", 3000), 2, 20000);
    var sceneCutThreshold = Math.Clamp(GetOptionalDouble(args, "scene_cut_threshold", 35), 1, 255);

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    var burstDir = Path.IsPathRooted(burstDirInput)
        ? Path.GetFullPath(burstDirInput)
        : Path.GetFullPath(Path.Combine(workspaceRoot, burstDirInput));

    if (!burstDir.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("burst_dir points outside of workspace_path.");
    }

    if (!Directory.Exists(burstDir))
    {
        throw new ArgumentException($"Burst directory does not exist: {burstDir}");
    }

    var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp"
    };

    var allFrames = Directory
        .EnumerateFiles(burstDir)
        .Where(path => supportedExtensions.Contains(Path.GetExtension(path)))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Take(maxFrames)
        .ToList();

    if (allFrames.Count < 2)
    {
        throw new ArgumentException("Not enough frames in burst_dir for analysis (need at least 2).");
    }

    var sampledFrames = allFrames
        .Where((_, index) => index % sampleEvery == 0)
        .ToList();

    if (sampledFrames.Count < 2)
    {
        sampledFrames = [allFrames[0], allFrames[^1]];
    }

    var timeline = new List<(string From, string To, double MotionScore, string MotionLevel, bool IsSceneCut)>(sampledFrames.Count - 1);
    var sumMotion = 0.0;
    var maxMotion = double.MinValue;
    var minMotion = double.MaxValue;
    var peaks = new List<(int Index, string Frame, double Score, string Level)>();

    using var prev = Cv2.ImRead(sampledFrames[0], ImreadModes.Grayscale);
    if (prev.Empty())
    {
        throw new ArgumentException($"Failed to read frame: {sampledFrames[0]}");
    }

    using var diff = new Mat();
    var previousFile = sampledFrames[0];
    var previous = prev.Clone();

    try
    {
        for (var i = 1; i < sampledFrames.Count; i++)
        {
            var currentFile = sampledFrames[i];
            using var current = Cv2.ImRead(currentFile, ImreadModes.Grayscale);
            if (current.Empty())
            {
                continue;
            }

            if (current.Size() != previous.Size())
            {
                Cv2.Resize(current, current, previous.Size());
            }

            Cv2.Absdiff(previous, current, diff);
            var motionScore = Cv2.Mean(diff).Val0;
            var level = ClassifyMotion(motionScore);

            timeline.Add((
                Path.GetFileName(previousFile),
                Path.GetFileName(currentFile),
                Math.Round(motionScore, 2),
                level,
                motionScore >= sceneCutThreshold));

            sumMotion += motionScore;
            maxMotion = Math.Max(maxMotion, motionScore);
            minMotion = Math.Min(minMotion, motionScore);
            peaks.Add((i, Path.GetFileName(currentFile), motionScore, level));

            current.CopyTo(previous);
            previousFile = currentFile;
        }
    }
    finally
    {
        previous.Dispose();
    }

    if (timeline.Count == 0)
    {
        throw new ArgumentException("Unable to analyze burst frames (no valid frame pairs).");
    }

    var avgMotion = sumMotion / timeline.Count;
    var topPeaks = peaks
        .OrderByDescending(item => item.Score)
        .Take(5)
        .Select(item => new
        {
            frame = item.Frame,
            motion_score = Math.Round(item.Score, 2),
            motion_level = item.Level
        })
        .ToList();

    var sceneCuts = timeline
        .Where(item => item.IsSceneCut)
        .Select(item => new { from = item.From, to = item.To, motion_score = item.MotionScore })
        .ToList();

    var timelineReport = timeline
        .Select(item => new
        {
            from = item.From,
            to = item.To,
            motion_score = item.MotionScore,
            motion_level = item.MotionLevel,
            is_scene_cut = item.IsSceneCut
        })
        .ToList();

    var summaryText = $"Analyzed {sampledFrames.Count} sampled frames from {allFrames.Count} total. " +
                      $"Motion avg={avgMotion:F2}, min={minMotion:F2}, max={maxMotion:F2}. " +
                      $"Scene cuts detected: {sceneCuts.Count}.";

    var result = new
    {
        success = true,
        burst_dir = burstDir,
        total_frames = allFrames.Count,
        sampled_frames = sampledFrames.Count,
        sample_every = sampleEvery,
        avg_motion_score = Math.Round(avgMotion, 2),
        min_motion_score = Math.Round(minMotion, 2),
        max_motion_score = Math.Round(maxMotion, 2),
        scene_cut_threshold = Math.Round(sceneCutThreshold, 2),
        scene_cut_count = sceneCuts.Count,
        top_motion_peaks = topPeaks,
        scene_cuts = sceneCuts,
        timeline = timelineReport,
        summary = summaryText,
        analyzed_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

static string HandleCaptureAudioBurst(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var durationSec = Math.Clamp(GetOptionalInt(args, "duration_sec", DefaultAudioDurationSec), 1, 300);
    var sampleRate = Math.Clamp(GetOptionalInt(args, "sample_rate", DefaultAudioSampleRate), 8000, 96000);
    var channels = Math.Clamp(GetOptionalInt(args, "channels", DefaultAudioChannels), 1, 2);
    var deviceNumber = Math.Clamp(GetOptionalInt(args, "device_number", 0), 0, 32);
    var outputSubdir = GetOptionalString(args, "output_subdir") ?? DefaultAudioOutputSubdir;
    var fileName = GetOptionalString(args, "file_name");

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    if (Path.IsPathRooted(outputSubdir))
    {
        throw new ArgumentException("output_subdir must be relative to workspace_path.");
    }

    var outputDir = Path.GetFullPath(Path.Combine(workspaceRoot, outputSubdir));
    if (!outputDir.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("output_subdir points outside of workspace_path.");
    }

    Directory.CreateDirectory(outputDir);

    var safeBaseName = string.IsNullOrWhiteSpace(fileName)
        ? $"audio-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}"
        : MakeSafeFileName(fileName);
    var outputPath = Path.Combine(outputDir, $"{safeBaseName}.wav");

    if (WaveInEvent.DeviceCount == 0)
    {
        throw new ArgumentException("No recording devices were found.");
    }

    if (deviceNumber >= WaveInEvent.DeviceCount)
    {
        throw new ArgumentException($"device_number {deviceNumber} is out of range. Available devices: {WaveInEvent.DeviceCount}.");
    }

    using var waveIn = new WaveInEvent
    {
        DeviceNumber = deviceNumber,
        WaveFormat = new WaveFormat(sampleRate, 16, channels),
        BufferMilliseconds = 50
    };

    using var writer = new WaveFileWriter(outputPath, waveIn.WaveFormat);
    using var completed = new ManualResetEventSlim(false);

    Exception? recordingError = null;

    waveIn.DataAvailable += (_, eventArgs) =>
    {
        writer.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
        writer.Flush();
    };

    waveIn.RecordingStopped += (_, eventArgs) =>
    {
        recordingError = eventArgs.Exception;
        completed.Set();
    };

    waveIn.StartRecording();
    Thread.Sleep(durationSec * 1000);
    waveIn.StopRecording();

    if (!completed.Wait(TimeSpan.FromSeconds(5)))
    {
        throw new ArgumentException("Timeout while finalizing audio recording.");
    }

    if (recordingError is not null)
    {
        throw new ArgumentException("Audio capture failed: " + recordingError.Message);
    }

    var fileInfo = new FileInfo(outputPath);
    if (!fileInfo.Exists || fileInfo.Length <= 44)
    {
        throw new ArgumentException("Recorded file is empty.");
    }

    var result = new
    {
        success = true,
        file_path = outputPath,
        duration_sec = durationSec,
        sample_rate = sampleRate,
        channels,
        device_number = deviceNumber,
        bytes = fileInfo.Length,
        captured_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

static string HandleAnalyzeAudioSequence(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var audioPathInput = GetRequiredString(args, "audio_path");
    var frameMs = Math.Clamp(GetOptionalInt(args, "frame_ms", DefaultAudioFrameMs), 10, 500);
    var silenceThresholdDb = Math.Clamp(GetOptionalDouble(args, "silence_threshold_db", DefaultAudioSilenceDb), -120, 0);

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    var audioPath = Path.IsPathRooted(audioPathInput)
        ? Path.GetFullPath(audioPathInput)
        : Path.GetFullPath(Path.Combine(workspaceRoot, audioPathInput));

    if (!audioPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("audio_path points outside of workspace_path.");
    }

    if (!File.Exists(audioPath))
    {
        throw new ArgumentException($"Audio file does not exist: {audioPath}");
    }

    using var reader = new AudioFileReader(audioPath);
    var sampleRate = reader.WaveFormat.SampleRate;
    var channels = reader.WaveFormat.Channels;
    var samplesPerFrame = Math.Max(1, sampleRate * frameMs / 1000);

    var readBuffer = new float[4096 * channels];
    var monoSamples = new List<float>(sampleRate * 10);
    float peakAbs = 0;

    int read;
    while ((read = reader.Read(readBuffer, 0, readBuffer.Length)) > 0)
    {
        for (var index = 0; index < read; index += channels)
        {
            var mono = 0.0f;
            for (var channel = 0; channel < channels && index + channel < read; channel++)
            {
                mono += readBuffer[index + channel];
            }

            mono /= channels;
            var abs = Math.Abs(mono);
            if (abs > peakAbs)
            {
                peakAbs = abs;
            }

            monoSamples.Add(mono);
        }
    }

    if (monoSamples.Count < 2)
    {
        throw new ArgumentException("Audio file is too short for analysis.");
    }

    var timeline = new List<object>();
    var rmsValues = new List<double>();
    var silentFrames = 0;
    var activeFrames = 0;
    var zeroCrossings = 0;

    for (var i = 1; i < monoSamples.Count; i++)
    {
        var prev = monoSamples[i - 1];
        var current = monoSamples[i];
        if ((prev >= 0 && current < 0) || (prev < 0 && current >= 0))
        {
            zeroCrossings++;
        }
    }

    for (var start = 0; start < monoSamples.Count; start += samplesPerFrame)
    {
        var end = Math.Min(monoSamples.Count, start + samplesPerFrame);
        var count = end - start;
        if (count <= 0)
        {
            continue;
        }

        double sumSquares = 0;
        float framePeak = 0;
        for (var i = start; i < end; i++)
        {
            var value = monoSamples[i];
            sumSquares += value * value;
            var abs = Math.Abs(value);
            if (abs > framePeak)
            {
                framePeak = abs;
            }
        }

        var rms = Math.Sqrt(sumSquares / count);
        var db = 20.0 * Math.Log10(Math.Max(1e-9, rms));
        var isSilent = db < silenceThresholdDb;
        if (isSilent)
        {
            silentFrames++;
        }
        else
        {
            activeFrames++;
        }

        rmsValues.Add(rms);
        timeline.Add(new
        {
            start_sec = Math.Round((double)start / sampleRate, 3),
            end_sec = Math.Round((double)end / sampleRate, 3),
            rms = Math.Round(rms, 5),
            dbfs = Math.Round(db, 2),
            peak = Math.Round(framePeak, 5),
            is_silent = isSilent
        });
    }

    var avgRms = rmsValues.Count > 0 ? rmsValues.Average() : 0;
    var maxRms = rmsValues.Count > 0 ? rmsValues.Max() : 0;
    var minRms = rmsValues.Count > 0 ? rmsValues.Min() : 0;
    var durationSeconds = (double)monoSamples.Count / sampleRate;
    var silenceRatio = timeline.Count > 0 ? (double)silentFrames / timeline.Count : 0;
    var activityRatio = timeline.Count > 0 ? (double)activeFrames / timeline.Count : 0;
    var zcr = durationSeconds > 0 ? zeroCrossings / durationSeconds : 0;
    var peakDb = 20.0 * Math.Log10(Math.Max(1e-9, peakAbs));

    var summary = $"Duration {durationSeconds:F2}s, activity {activityRatio:P0}, silence {silenceRatio:P0}, " +
                  $"avg level {20 * Math.Log10(Math.Max(1e-9, avgRms)):F1} dBFS, peak {peakDb:F1} dBFS.";

    var result = new
    {
        success = true,
        audio_path = audioPath,
        sample_rate = sampleRate,
        channels,
        duration_sec = Math.Round(durationSeconds, 3),
        frame_ms = frameMs,
        silence_threshold_db = silenceThresholdDb,
        avg_rms = Math.Round(avgRms, 6),
        min_rms = Math.Round(minRms, 6),
        max_rms = Math.Round(maxRms, 6),
        peak = Math.Round(peakAbs, 6),
        peak_dbfs = Math.Round(peakDb, 2),
        silence_ratio = Math.Round(silenceRatio, 4),
        activity_ratio = Math.Round(activityRatio, 4),
        zero_crossings_per_sec = Math.Round(zcr, 2),
        total_frames = timeline.Count,
        timeline,
        summary,
        analyzed_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

static string HandleTranscribeAudioWhisper(IReadOnlyDictionary<string, JsonElement> args) =>
    HandleTranscribeAudioWhisperAsync(args).GetAwaiter().GetResult();

static async Task<string> HandleTranscribeAudioWhisperAsync(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var audioPathInput = GetRequiredString(args, "audio_path");
    var modelPathInput = GetOptionalString(args, "model_path");
    var language = (GetOptionalString(args, "language") ?? "auto").Trim().ToLowerInvariant();
    var maxSegments = Math.Clamp(GetOptionalInt(args, "max_segments", 1000), 1, 5000);

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    var audioPath = Path.IsPathRooted(audioPathInput)
        ? Path.GetFullPath(audioPathInput)
        : Path.GetFullPath(Path.Combine(workspaceRoot, audioPathInput));

    if (!audioPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("audio_path points outside of workspace_path.");
    }

    if (!File.Exists(audioPath))
    {
        throw new ArgumentException($"Audio file does not exist: {audioPath}");
    }

    var modelPath = modelPathInput;
    if (string.IsNullOrWhiteSpace(modelPath))
    {
        modelPath = Environment.GetEnvironmentVariable(EnvWhisperModelPath);
    }

    if (string.IsNullOrWhiteSpace(modelPath))
    {
        throw new ArgumentException("model_path is required or set WHISPER_MODEL_PATH env var.");
    }

    modelPath = Path.GetFullPath(modelPath.Trim());
    if (!File.Exists(modelPath))
    {
        throw new ArgumentException($"Whisper model not found: {modelPath}");
    }

    var tempDir = Path.Combine(workspaceRoot, ".cascade-ide", "audio-captures");
    Directory.CreateDirectory(tempDir);
    var normalizedWavPath = Path.Combine(tempDir, $"whisper-input-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.wav");

    // Whisper runtime is most reliable on 16kHz mono PCM WAV.
    using (var reader = new AudioFileReader(audioPath))
    {
        ISampleProvider sampleProvider = reader;
        if (reader.WaveFormat.Channels == 2)
        {
            var stereoToMono = new StereoToMonoSampleProvider(sampleProvider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
            sampleProvider = stereoToMono;
        }
        else if (reader.WaveFormat.Channels > 2)
        {
            throw new ArgumentException($"Unsupported channel count: {reader.WaveFormat.Channels}. Use mono/stereo source.");
        }

        var resampled = new WdlResamplingSampleProvider(sampleProvider, 16000);
        WaveFileWriter.CreateWaveFile16(normalizedWavPath, resampled);
    }

    var segments = new List<object>();
    var transcriptParts = new List<string>();

    try
    {
        using var whisperFactory = WhisperFactory.FromPath(modelPath);
        using var processor = whisperFactory
            .CreateBuilder()
            .WithLanguage(language)
            .Build();

        await using var fileStream = File.OpenRead(normalizedWavPath);
        await foreach (var segment in processor.ProcessAsync(fileStream))
        {
            var text = segment.Text?.Trim() ?? string.Empty;
            if (text.Length > 0)
            {
                transcriptParts.Add(text);
            }

            if (segments.Count < maxSegments)
            {
                segments.Add(new
                {
                    start_sec = Math.Round(segment.Start.TotalSeconds, 3),
                    end_sec = Math.Round(segment.End.TotalSeconds, 3),
                    text
                });
            }
        }
    }
    finally
    {
        try
        {
            if (File.Exists(normalizedWavPath))
            {
                File.Delete(normalizedWavPath);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    var transcript = string.Join(" ", transcriptParts).Trim();
    var result = new
    {
        success = true,
        audio_path = audioPath,
        model_path = modelPath,
        language,
        transcript,
        segments,
        segment_count = segments.Count,
        transcribed_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

static string HandleCaptureAvBurst(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var durationSec = Math.Clamp(GetOptionalInt(args, "duration_sec", DefaultAudioDurationSec), 1, 300);
    var targetFps = Math.Clamp(GetOptionalInt(args, "target_fps", DefaultBurstTargetFps), 1, 120);
    var cameraIndex = GetOptionalInt(args, "camera_index", 0);
    var audioDeviceNumber = GetOptionalInt(args, "audio_device_number", 0);
    var requestedWidth = GetOptionalInt(args, "width", 0);
    var requestedHeight = GetOptionalInt(args, "height", 0);
    var audioSampleRate = Math.Clamp(GetOptionalInt(args, "audio_sample_rate", DefaultAudioSampleRate), 8000, 96000);
    var audioChannels = Math.Clamp(GetOptionalInt(args, "audio_channels", DefaultAudioChannels), 1, 2);
    var warmupFrames = Math.Clamp(GetOptionalInt(args, "warmup_frames", DefaultWarmupFrames), 0, 50);
    var imageFormat = NormalizeImageFormat(GetOptionalString(args, "image_format") ?? "jpg");
    var jpegQuality = Math.Clamp(GetOptionalInt(args, "jpeg_quality", DefaultJpegQuality), 1, 100);
    var outputSubdir = GetOptionalString(args, "output_subdir") ?? DefaultAvOutputSubdir;
    var sessionName = GetOptionalString(args, "session_name");
    var saveVideo = GetOptionalBool(args, "save_video", true);
    var videoFps = Math.Clamp(GetOptionalInt(args, "video_fps", DefaultBurstVideoFps), 1, 120);

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    if (Path.IsPathRooted(outputSubdir))
    {
        throw new ArgumentException("output_subdir must be relative to workspace_path.");
    }

    var outputDir = Path.GetFullPath(Path.Combine(workspaceRoot, outputSubdir));
    if (!outputDir.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("output_subdir points outside of workspace_path.");
    }

    Directory.CreateDirectory(outputDir);

    var safeSessionName = string.IsNullOrWhiteSpace(sessionName)
        ? $"av-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}"
        : MakeSafeFileName(sessionName);
    var sessionDir = Path.Combine(outputDir, safeSessionName);
    var framesDir = Path.Combine(sessionDir, "frames");
    Directory.CreateDirectory(framesDir);

    var audioPath = Path.Combine(sessionDir, "audio.wav");
    var metadataPath = Path.Combine(sessionDir, "metadata.json");
    var videoPath = saveVideo ? Path.Combine(sessionDir, "video.mp4") : null;

    if (WaveInEvent.DeviceCount == 0)
    {
        throw new ArgumentException("No recording devices were found.");
    }

    if (audioDeviceNumber < 0 || audioDeviceNumber >= WaveInEvent.DeviceCount)
    {
        throw new ArgumentException($"audio_device_number {audioDeviceNumber} is out of range. Available devices: {WaveInEvent.DeviceCount}.");
    }

    using var capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.ANY);
    if (!capture.IsOpened())
    {
        throw new ArgumentException($"Camera {cameraIndex} is not available.");
    }

    if (requestedWidth > 0)
    {
        capture.Set(VideoCaptureProperties.FrameWidth, requestedWidth);
    }

    if (requestedHeight > 0)
    {
        capture.Set(VideoCaptureProperties.FrameHeight, requestedHeight);
    }

    using var waveIn = new WaveInEvent
    {
        DeviceNumber = audioDeviceNumber,
        WaveFormat = new WaveFormat(audioSampleRate, 16, audioChannels),
        BufferMilliseconds = 50
    };
    using var audioWriter = new WaveFileWriter(audioPath, waveIn.WaveFormat);
    using var audioCompleted = new ManualResetEventSlim(false);

    Exception? audioError = null;
    var audioLock = new object();

    waveIn.DataAvailable += (_, eventArgs) =>
    {
        lock (audioLock)
        {
            audioWriter.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
            audioWriter.Flush();
        }
    };

    waveIn.RecordingStopped += (_, eventArgs) =>
    {
        audioError = eventArgs.Exception;
        audioCompleted.Set();
    };

    using var frame = new Mat();
    VideoWriter? videoWriter = null;
    var frameTimestampsMs = new List<int>();
    var frameCount = 0;

    var startUtc = DateTime.UtcNow;
    var durationMs = durationSec * 1000.0;
    var intervalMs = 1000.0 / targetFps;
    var stopwatch = Stopwatch.StartNew();
    var nextCaptureAt = 0.0;

    try
    {
        for (var i = 0; i < warmupFrames; i++)
        {
            capture.Read(frame);
            Thread.Sleep(15);
        }

        waveIn.StartRecording();

        while (stopwatch.Elapsed.TotalMilliseconds <= durationMs)
        {
            var elapsed = stopwatch.Elapsed.TotalMilliseconds;
            if (elapsed < nextCaptureAt)
            {
                var waitMs = Math.Max(1, (int)(nextCaptureAt - elapsed));
                Thread.Sleep(Math.Min(waitMs, 5));
                continue;
            }

            if (!capture.Read(frame) || frame.Empty())
            {
                nextCaptureAt += intervalMs;
                continue;
            }

            frameCount++;
            var frameFileName = $"{frameCount:D5}.{imageFormat}";
            var framePath = Path.Combine(framesDir, frameFileName);
            var saved = imageFormat switch
            {
                "jpg" => Cv2.ImWrite(framePath, frame, [new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality)]),
                "png" => Cv2.ImWrite(framePath, frame),
                _ => false
            };

            if (!saved)
            {
                throw new ArgumentException($"Failed to save video frame: {framePath}");
            }

            if (saveVideo)
            {
                if (videoWriter is null)
                {
                    videoWriter = new VideoWriter(
                        videoPath!,
                        VideoWriter.FourCC('m', 'p', '4', 'v'),
                        videoFps,
                        new Size(frame.Width, frame.Height));
                    if (!videoWriter.IsOpened())
                    {
                        videoWriter.Dispose();
                        videoWriter = null;
                        throw new ArgumentException("Failed to initialize MP4 writer for A/V capture.");
                    }
                }

                videoWriter.Write(frame);
            }

            frameTimestampsMs.Add((int)Math.Round(elapsed));
            nextCaptureAt += intervalMs;
        }
    }
    finally
    {
        waveIn.StopRecording();
        videoWriter?.Release();
        videoWriter?.Dispose();
    }

    if (!audioCompleted.Wait(TimeSpan.FromSeconds(8)))
    {
        throw new ArgumentException("Timeout while finalizing audio recording.");
    }

    if (audioError is not null)
    {
        throw new ArgumentException("Audio capture failed: " + audioError.Message);
    }

    var audioInfo = new FileInfo(audioPath);
    if (!audioInfo.Exists || audioInfo.Length <= 44)
    {
        throw new ArgumentException("A/V capture produced empty audio track.");
    }

    if (frameCount == 0)
    {
        throw new ArgumentException("A/V capture produced no video frames.");
    }

    var actualDurationMs = stopwatch.Elapsed.TotalMilliseconds;
    var actualFps = actualDurationMs > 0 ? frameCount * 1000.0 / actualDurationMs : 0;
    var metadata = new
    {
        session_dir = sessionDir,
        start_utc = startUtc.ToString("O"),
        requested_duration_sec = durationSec,
        actual_duration_ms = (int)Math.Round(actualDurationMs),
        camera_index = cameraIndex,
        audio_device_number = audioDeviceNumber,
        frame_width = frame.Width,
        frame_height = frame.Height,
        frame_format = imageFormat,
        frame_count = frameCount,
        frame_timestamps_ms = frameTimestampsMs,
        target_fps = targetFps,
        actual_fps = Math.Round(actualFps, 2),
        audio_path = audioPath,
        video_path = videoPath
    };
    File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

    var result = new
    {
        success = true,
        session_dir = sessionDir,
        frames_dir = framesDir,
        audio_path = audioPath,
        video_path = videoPath,
        metadata_path = metadataPath,
        frame_count = frameCount,
        actual_fps = Math.Round(actualFps, 2),
        duration_sec = durationSec,
        captured_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

static string HandleCaptureScreenAvBurst(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var durationSec = Math.Clamp(GetOptionalInt(args, "duration_sec", DefaultAudioDurationSec), 1, 300);
    var targetFps = Math.Clamp(GetOptionalInt(args, "target_fps", DefaultBurstTargetFps), 1, 120);
    var audioDeviceNumber = GetOptionalInt(args, "audio_device_number", 0);
    var monitorNumber = GetOptionalMonitorNumber(args, "monitor");
    var hasExplicitRegion = args.ContainsKey("x") || args.ContainsKey("y") || args.ContainsKey("width") || args.ContainsKey("height");
    var region = ResolveCaptureRegion(monitorNumber, hasExplicitRegion);
    var captureX = GetOptionalInt(args, "x", region.X);
    var captureY = GetOptionalInt(args, "y", region.Y);
    var captureWidth = Math.Max(1, GetOptionalInt(args, "width", region.Width));
    var captureHeight = Math.Max(1, GetOptionalInt(args, "height", region.Height));
    var audioSampleRate = Math.Clamp(GetOptionalInt(args, "audio_sample_rate", DefaultAudioSampleRate), 8000, 96000);
    var audioChannels = Math.Clamp(GetOptionalInt(args, "audio_channels", DefaultAudioChannels), 1, 2);
    var imageFormat = NormalizeImageFormat(GetOptionalString(args, "image_format") ?? "jpg");
    var jpegQuality = Math.Clamp(GetOptionalInt(args, "jpeg_quality", DefaultJpegQuality), 1, 100);
    var outputSubdir = GetOptionalString(args, "output_subdir") ?? DefaultAvOutputSubdir;
    var sessionName = GetOptionalString(args, "session_name");
    var saveVideo = GetOptionalBool(args, "save_video", true);
    var videoFps = Math.Clamp(GetOptionalInt(args, "video_fps", DefaultBurstVideoFps), 1, 120);

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    if (Path.IsPathRooted(outputSubdir))
    {
        throw new ArgumentException("output_subdir must be relative to workspace_path.");
    }

    var outputDir = Path.GetFullPath(Path.Combine(workspaceRoot, outputSubdir));
    if (!outputDir.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("output_subdir points outside of workspace_path.");
    }

    Directory.CreateDirectory(outputDir);

    var safeSessionName = string.IsNullOrWhiteSpace(sessionName)
        ? $"screen-av-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}"
        : MakeSafeFileName(sessionName);
    var sessionDir = Path.Combine(outputDir, safeSessionName);
    var framesDir = Path.Combine(sessionDir, "frames");
    Directory.CreateDirectory(framesDir);

    var audioPath = Path.Combine(sessionDir, "audio.wav");
    var metadataPath = Path.Combine(sessionDir, "metadata.json");
    var videoPath = saveVideo ? Path.Combine(sessionDir, "video.mp4") : null;

    if (WaveInEvent.DeviceCount == 0)
    {
        throw new ArgumentException("No recording devices were found.");
    }

    if (audioDeviceNumber < 0 || audioDeviceNumber >= WaveInEvent.DeviceCount)
    {
        throw new ArgumentException($"audio_device_number {audioDeviceNumber} is out of range. Available devices: {WaveInEvent.DeviceCount}.");
    }

    using var waveIn = new WaveInEvent
    {
        DeviceNumber = audioDeviceNumber,
        WaveFormat = new WaveFormat(audioSampleRate, 16, audioChannels),
        BufferMilliseconds = 50
    };
    using var audioWriter = new WaveFileWriter(audioPath, waveIn.WaveFormat);
    using var audioCompleted = new ManualResetEventSlim(false);

    Exception? audioError = null;
    var audioLock = new object();
    waveIn.DataAvailable += (_, eventArgs) =>
    {
        lock (audioLock)
        {
            audioWriter.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
            audioWriter.Flush();
        }
    };
    waveIn.RecordingStopped += (_, eventArgs) =>
    {
        audioError = eventArgs.Exception;
        audioCompleted.Set();
    };

    VideoWriter? videoWriter = null;
    var frameTimestampsMs = new List<int>();
    var frameCount = 0;
    var frameWidth = captureWidth;
    var frameHeight = captureHeight;

    var startUtc = DateTime.UtcNow;
    var durationMs = durationSec * 1000.0;
    var intervalMs = 1000.0 / targetFps;
    var stopwatch = Stopwatch.StartNew();
    var nextCaptureAt = 0.0;

    try
    {
        waveIn.StartRecording();
        while (stopwatch.Elapsed.TotalMilliseconds <= durationMs)
        {
            var elapsed = stopwatch.Elapsed.TotalMilliseconds;
            if (elapsed < nextCaptureAt)
            {
                var waitMs = Math.Max(1, (int)(nextCaptureAt - elapsed));
                Thread.Sleep(Math.Min(waitMs, 5));
                continue;
            }

            using var bitmap = new System.Drawing.Bitmap(
                captureWidth,
                captureHeight,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    captureX,
                    captureY,
                    0,
                    0,
                    new System.Drawing.Size(captureWidth, captureHeight),
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }

            frameCount++;
            var frameFileName = $"{frameCount:D5}.{imageFormat}";
            var framePath = Path.Combine(framesDir, frameFileName);
            SaveBitmapToPath(bitmap, framePath, imageFormat, jpegQuality);

            if (saveVideo)
            {
                using var frameMat = Cv2.ImRead(framePath, ImreadModes.Color);
                if (frameMat.Empty())
                {
                    throw new ArgumentException($"Failed to read saved frame for video: {framePath}");
                }

                frameWidth = frameMat.Width;
                frameHeight = frameMat.Height;

                if (videoWriter is null)
                {
                    videoWriter = new VideoWriter(
                        videoPath!,
                        VideoWriter.FourCC('m', 'p', '4', 'v'),
                        videoFps,
                        new Size(frameMat.Width, frameMat.Height));
                    if (!videoWriter.IsOpened())
                    {
                        videoWriter.Dispose();
                        videoWriter = null;
                        throw new ArgumentException("Failed to initialize MP4 writer for screen A/V capture.");
                    }
                }

                videoWriter.Write(frameMat);
            }

            frameTimestampsMs.Add((int)Math.Round(elapsed));
            nextCaptureAt += intervalMs;
        }
    }
    finally
    {
        waveIn.StopRecording();
        videoWriter?.Release();
        videoWriter?.Dispose();
    }

    if (!audioCompleted.Wait(TimeSpan.FromSeconds(8)))
    {
        throw new ArgumentException("Timeout while finalizing audio recording.");
    }

    if (audioError is not null)
    {
        throw new ArgumentException("Audio capture failed: " + audioError.Message);
    }

    var audioInfo = new FileInfo(audioPath);
    if (!audioInfo.Exists || audioInfo.Length <= 44)
    {
        throw new ArgumentException("Screen A/V capture produced empty audio track.");
    }

    if (frameCount == 0)
    {
        throw new ArgumentException("Screen A/V capture produced no video frames.");
    }

    var actualDurationMs = stopwatch.Elapsed.TotalMilliseconds;
    var actualFps = actualDurationMs > 0 ? frameCount * 1000.0 / actualDurationMs : 0;
    var metadata = new
    {
        session_dir = sessionDir,
        source = "screen",
        start_utc = startUtc.ToString("O"),
        requested_duration_sec = durationSec,
        actual_duration_ms = (int)Math.Round(actualDurationMs),
        capture_region = new { x = captureX, y = captureY, width = captureWidth, height = captureHeight },
        audio_device_number = audioDeviceNumber,
        frame_width = frameWidth,
        frame_height = frameHeight,
        frame_format = imageFormat,
        frame_count = frameCount,
        frame_timestamps_ms = frameTimestampsMs,
        target_fps = targetFps,
        actual_fps = Math.Round(actualFps, 2),
        audio_path = audioPath,
        video_path = videoPath
    };
    File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

    var result = new
    {
        success = true,
        session_dir = sessionDir,
        frames_dir = framesDir,
        audio_path = audioPath,
        video_path = videoPath,
        metadata_path = metadataPath,
        frame_count = frameCount,
        actual_fps = Math.Round(actualFps, 2),
        duration_sec = durationSec,
        capture_region = new { x = captureX, y = captureY, width = captureWidth, height = captureHeight },
        captured_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

static string HandleAnalyzeAvSequence(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var sessionDirInput = GetRequiredString(args, "session_dir");
    var sampleEvery = Math.Clamp(GetOptionalInt(args, "sample_every", 1), 1, 120);
    var maxFrames = Math.Clamp(GetOptionalInt(args, "max_frames", 3000), 2, 20000);
    var sceneCutThreshold = Math.Clamp(GetOptionalDouble(args, "scene_cut_threshold", 35), 1, 255);
    var audioFrameMs = Math.Clamp(GetOptionalInt(args, "audio_frame_ms", DefaultAudioFrameMs), 10, 500);
    var silenceThresholdDb = Math.Clamp(GetOptionalDouble(args, "silence_threshold_db", DefaultAudioSilenceDb), -120, 0);

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    var sessionDir = Path.IsPathRooted(sessionDirInput)
        ? Path.GetFullPath(sessionDirInput)
        : Path.GetFullPath(Path.Combine(workspaceRoot, sessionDirInput));

    if (!sessionDir.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("session_dir points outside of workspace_path.");
    }

    if (!Directory.Exists(sessionDir))
    {
        throw new ArgumentException($"Session directory does not exist: {sessionDir}");
    }

    var framesDir = Path.Combine(sessionDir, "frames");
    var audioPath = Path.Combine(sessionDir, "audio.wav");
    if (!Directory.Exists(framesDir))
    {
        throw new ArgumentException($"Frames directory not found: {framesDir}");
    }

    if (!File.Exists(audioPath))
    {
        throw new ArgumentException($"Audio track not found: {audioPath}");
    }

    var videoArgs = new Dictionary<string, JsonElement>
    {
        ["workspace_path"] = JsonSerializer.SerializeToElement(workspaceRoot),
        ["burst_dir"] = JsonSerializer.SerializeToElement(framesDir),
        ["sample_every"] = JsonSerializer.SerializeToElement(sampleEvery),
        ["max_frames"] = JsonSerializer.SerializeToElement(maxFrames),
        ["scene_cut_threshold"] = JsonSerializer.SerializeToElement(sceneCutThreshold)
    };
    var audioArgs = new Dictionary<string, JsonElement>
    {
        ["workspace_path"] = JsonSerializer.SerializeToElement(workspaceRoot),
        ["audio_path"] = JsonSerializer.SerializeToElement(audioPath),
        ["frame_ms"] = JsonSerializer.SerializeToElement(audioFrameMs),
        ["silence_threshold_db"] = JsonSerializer.SerializeToElement(silenceThresholdDb)
    };

    var videoJson = HandleAnalyzeBurstSequence(videoArgs);
    var audioJson = HandleAnalyzeAudioSequence(audioArgs);
    using var videoDoc = JsonDocument.Parse(videoJson);
    using var audioDoc = JsonDocument.Parse(audioJson);

    var videoRoot = videoDoc.RootElement.Clone();
    var audioRoot = audioDoc.RootElement.Clone();
    var videoSummary = videoRoot.TryGetProperty("summary", out var vs) ? vs.GetString() ?? "" : "";
    var audioSummary = audioRoot.TryGetProperty("summary", out var @as) ? @as.GetString() ?? "" : "";
    var avgMotion = videoRoot.TryGetProperty("avg_motion_score", out var am) ? am.GetDouble() : 0;
    var activityRatio = audioRoot.TryGetProperty("activity_ratio", out var ar) ? ar.GetDouble() : 0;

    var combinedLabel = avgMotion switch
    {
        < 3 when activityRatio < 0.4 => "calm_silent",
        < 3 when activityRatio < 0.7 => "calm_talk",
        < 6 when activityRatio < 0.7 => "active_talk",
        _ => "dynamic"
    };

    var result = new
    {
        success = true,
        session_dir = sessionDir,
        av_profile = combinedLabel,
        summary = $"Video: {videoSummary} Audio: {audioSummary}",
        video_analysis = videoRoot,
        audio_analysis = audioRoot,
        analyzed_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

static (int X, int Y, int Width, int Height) ResolveCaptureRegion(int? monitorNumber, bool hasExplicitRegion)
{
    if (!hasExplicitRegion && monitorNumber.HasValue)
    {
        var monitor = GetMonitorRegion(monitorNumber.Value);
        return (monitor.Left, monitor.Top, monitor.Width, monitor.Height);
    }

    return (
        GetSystemMetrics(SmXVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen),
        GetSystemMetrics(SmCxVirtualScreen),
        GetSystemMetrics(SmCyVirtualScreen)
    );
}

static WinRect GetMonitorRegion(int monitorNumber)
{
    var monitors = EnumerateMonitors();
    if (monitors.Count == 0)
    {
        throw new ArgumentException("No monitors were detected.");
    }

    if (monitorNumber < 1 || monitorNumber > monitors.Count)
    {
        throw new ArgumentException($"monitor {monitorNumber} is out of range. Available monitors: 1..{monitors.Count}.");
    }

    return monitors[monitorNumber - 1];
}

static List<WinRect> EnumerateMonitors()
{
    var monitors = new List<WinRect>();
    MonitorEnumProc callback = (IntPtr _, IntPtr _, ref WinRect rect, IntPtr _) =>
    {
        monitors.Add(rect);
        return true;
    };

    if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
    {
        throw new ArgumentException("Failed to enumerate display monitors.");
    }

    return monitors
        .OrderBy(m => m.Left)
        .ThenBy(m => m.Top)
        .ToList();
}

static void SaveBitmapToPath(System.Drawing.Bitmap bitmap, string outputPath, string imageFormat, int jpegQuality)
{
    if (imageFormat == "png")
    {
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        return;
    }

    var codec = GetImageCodec("image/jpeg");
    if (codec is null)
    {
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Jpeg);
        return;
    }

    using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
    parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
        System.Drawing.Imaging.Encoder.Quality,
        (long)Math.Clamp(jpegQuality, 1, 100));
    bitmap.Save(outputPath, codec, parameters);
}

static System.Drawing.Imaging.ImageCodecInfo? GetImageCodec(string mimeType) =>
    System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
        .FirstOrDefault(codec => string.Equals(codec.MimeType, mimeType, StringComparison.OrdinalIgnoreCase));

static string GetRequiredString(IReadOnlyDictionary<string, JsonElement> args, string key)
{
    if (!args.TryGetValue(key, out var raw))
    {
        throw new ArgumentException($"{key} is required.");
    }

    var value = raw.GetString();
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new ArgumentException($"{key} is required.");
    }

    return value;
}

static int GetOptionalInt(IReadOnlyDictionary<string, JsonElement> args, string key, int fallback)
{
    if (!args.TryGetValue(key, out var raw))
    {
        return fallback;
    }

    return raw.ValueKind == JsonValueKind.Number && raw.TryGetInt32(out var value)
        ? value
        : fallback;
}

static int? GetOptionalMonitorNumber(IReadOnlyDictionary<string, JsonElement> args, string key)
{
    if (!args.TryGetValue(key, out var raw))
    {
        return null;
    }

    if (raw.ValueKind == JsonValueKind.Number && raw.TryGetInt32(out var numeric))
    {
        if (numeric == 0)
        {
            return null;
        }

        if (numeric > 0)
        {
            return numeric;
        }

        throw new ArgumentException($"{key} must be a positive monitor number or 'all'.");
    }

    if (raw.ValueKind == JsonValueKind.String)
    {
        var value = raw.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        throw new ArgumentException($"{key} must be a positive monitor number or 'all'.");
    }

    throw new ArgumentException($"{key} must be a positive monitor number or 'all'.");
}

static bool GetOptionalBool(IReadOnlyDictionary<string, JsonElement> args, string key, bool fallback)
{
    if (!args.TryGetValue(key, out var raw))
    {
        return fallback;
    }

    return raw.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => fallback
    };
}

static double GetOptionalDouble(IReadOnlyDictionary<string, JsonElement> args, string key, double fallback)
{
    if (!args.TryGetValue(key, out var raw))
    {
        return fallback;
    }

    return raw.ValueKind == JsonValueKind.Number && raw.TryGetDouble(out var value)
        ? value
        : fallback;
}

static string? GetOptionalString(IReadOnlyDictionary<string, JsonElement> args, string key)
{
    if (!args.TryGetValue(key, out var raw))
    {
        return null;
    }

    return raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
}

static string NormalizeImageFormat(string value)
{
    var normalized = value.Trim().ToLowerInvariant();
    return normalized switch
    {
        "jpeg" => "jpg",
        "jpg" => "jpg",
        "png" => "png",
        _ => throw new ArgumentException("image_format must be 'jpg' or 'png'.")
    };
}

static string NormalizeVideoFormat(string value)
{
    var normalized = value.Trim().ToLowerInvariant();
    return normalized switch
    {
        "mp4" => "mp4",
        "avi" => "avi",
        _ => throw new ArgumentException("video_format must be 'mp4' or 'avi'.")
    };
}

static string MakeSafeFileName(string baseName)
{
    var invalidChars = Path.GetInvalidFileNameChars();
    var filtered = new string(baseName.Trim().Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
    return string.IsNullOrWhiteSpace(filtered) ? $"webcam-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}" : filtered;
}

static string ClassifyMotion(double score)
{
    if (score < 5)
    {
        return "still";
    }

    if (score < 15)
    {
        return "low";
    }

    if (score < 35)
    {
        return "medium";
    }

    return "high";
}

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "WebcamMcp", Version = "0.1.0" },
    ProtocolVersion = "2024-11-05",
    Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
    Handlers = new McpServerHandlers
    {
        ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = toolsList }),

        CallToolHandler = (request, _) =>
        {
            var name = request.Params?.Name ?? "";
            var args = request.Params?.Arguments is IReadOnlyDictionary<string, JsonElement> a
                ? a
                : FrozenDictionary<string, JsonElement>.Empty;

            try
            {
                var text = name switch
                {
                    "capture_webcam_frame" => HandleCaptureWebcamFrame(args),
                    "capture_webcam_burst" => HandleCaptureWebcamBurst(args),
                    "capture_screen_burst" => HandleCaptureScreenBurst(args),
                    "analyze_burst_sequence" => HandleAnalyzeBurstSequence(args),
                    "capture_audio_burst" => HandleCaptureAudioBurst(args),
                    "analyze_audio_sequence" => HandleAnalyzeAudioSequence(args),
                    "transcribe_audio_whisper" => HandleTranscribeAudioWhisper(args),
                    "capture_av_burst" => HandleCaptureAvBurst(args),
                    "capture_screen_av_burst" => HandleCaptureScreenAvBurst(args),
                    "analyze_av_sequence" => HandleAnalyzeAvSequence(args),
                    _ => throw new ArgumentException($"Unknown tool: {name}.")
                };

                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = text }],
                    IsError = false
                });
            }
            catch (ArgumentException ex)
            {
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Error: {ex.Message}" }],
                    IsError = true
                });
            }
            catch (Exception ex)
            {
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "Error: " + ex.Message }],
                    IsError = true
                });
            }
        }
    }
};

var transport = new StdioServerTransport("WebcamMcp");
await using var server = McpServer.Create(transport, options);
await server.RunAsync();
return 0;

delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref WinRect lprcMonitor, IntPtr dwData);

[StructLayout(LayoutKind.Sequential)]
struct WinRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}
