Callspire — ffmpeg (для записи звонков)

Зачем
-----
Без ffmpeg записи остаются в PCM / WebM; WAV и микширование не выполняются:
  • SIP    — PCM inbound/outbound → WAV
  • WebRTC — WebM → WAV / MP3

Структура в репозитории (dev / publish)
---------------------------------------
tools/
  README_FFMPEG.txt          ← этот файл
  Windows/
    ffmpeg-…-essentials_build/
      bin/ffmpeg.exe         ← Windows x64
  MacOS/
    ffmpeg                   ← macOS binary (без расширения)

При dotnet build / publish папка tools/ копируется рядом с Callspire.exe.
Приложение ищет ffmpeg так:

  1. tools/<платформа>/…  (Windows → tools/Windows/, macOS → tools/MacOS/)
  2. tools/ffmpeg(.exe)     (плоская раскладка)
  3. Системный PATH

Как положить бинарники
----------------------
Windows
  • essentials build с https://www.gyan.dev/ffmpeg/builds/
  • Распаковать в tools/Windows/ (как ffmpeg-…-essentials_build/bin/ffmpeg.exe)

macOS
  • cp $(which ffmpeg) tools/MacOS/ffmpeg
  • или brew install ffmpeg (PATH, без tools/)

Linux
  • tools/Linux/ffmpeg  или  apt/dnf install ffmpeg

В git
-----
Бинарники ffmpeg большие и не коммитятся (.gitignore).
В репозитории остаётся только README и структура папок — бинарники кладите локально
перед build/publish (или на CI перед упаковкой).

Без ffmpeg звонки работают; записи могут остаться без финального WAV.
