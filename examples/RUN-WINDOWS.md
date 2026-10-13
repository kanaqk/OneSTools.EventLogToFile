# Запуск на Windows x64 без установки .NET

Распакуйте **весь ZIP**, например в `C:\EventLogExporter`. Не переносите только EXE:
библиотеки и runtime из архива также нужны приложению.

Откройте `appsettings.json` и укажите:

```json
"Exporter": {
  "StorageType": 3,
  "LogFolder": "D:\\1C\\1Cv8Log",
  "Portion": 10000,
  "TimeZone": "Europe/Moscow",
  "WritingMaxDegreeOfParallelism": 1,
  "CollectedFactor": 3,
  "ReadingTimeout": 1,
  "LoadArchive": false,
  "SkipEventsBeforeDate": "0001-01-01T00:00:00"
},
"JsonFile": {
  "Path": "D:\\Logs\\1C\\events.jsonl"
}
```

Это секции существующего конфигурационного файла: замените их, сохранив остальные
секции и внешние фигурные скобки. В JSON обратные слеши в путях удваиваются.
Выберите часовой пояс, в котором 1С записывает журнал.
В исходном каталоге должны быть `1Cv8.lgf` и файлы `*.lgp`.
Учётной записи запуска нужны права чтения журнала и записи выходного каталога.

Запуск в PowerShell:

```powershell
cd C:\EventLogExporter
.\EventLogExporter.exe
```

Дополнительные переменные окружения и команда `dotnet` для этой сборки не нужны.
Первый запуск прочитает имеющийся журнал и продолжит ожидать новые записи.
Остановка — Ctrl+C. Перезапуск с тем же выходным файлом продолжит экспорт с последней
сохранённой позиции. Файл `events.jsonl` предназначен для Filebeat;
в `filebeat.example.yml` замените пути на Windows-пути, например `D:/Logs/1C/events.jsonl`,
и укажите свой адрес Elasticsearch.

Для однократного чтения с начала и завершения приложения:

```powershell
.\EventLogExporter.exe --Exporter:LoadArchive=true
```

Повторный запуск с `LoadArchive=true` снова добавляет весь журнал в выходной файл.
Обычный запуск использует `LoadArchive=false` из конфигурации.

Эта сборка сохраняет исходную целевую платформу проекта: .NET 5; runtime включён в ZIP.
