# Запуск компактной сборки на Windows x64

Для этой сборки требуется установленный **Microsoft.NETCore.App 5.0.17 x64**.
Проверьте наличие runtime командой `dotnet --list-runtimes` или
`& "C:\Program Files\dotnet\dotnet.exe" --list-runtimes` в PowerShell.
SDK для сборки на сервере не нужен.

Распакуйте **весь ZIP**, например в `C:\EventLogExporter`. Не переносите только EXE:
библиотеки из архива также нужны приложению.

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
  "Folder": "D:\\Logs\\1C"
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
Остановка — Ctrl+C. Перезапуск с той же выходной папкой продолжит экспорт с сохранённой позиции.
Каждый `*.lgp` создаёт соответствующий `*.jsonl`, например `20261013000000.jsonl`.
В `filebeat.example.yml` замените пути на Windows-маску, например `D:/Logs/1C/*.jsonl`,
и укажите свой адрес Elasticsearch.

Для однократного чтения с начала и завершения приложения:

```powershell
.\EventLogExporter.exe --Exporter:LoadArchive=true
```

Повторный запуск с `LoadArchive=true` снова добавляет весь журнал в соответствующие выходные файлы.
Обычный запуск использует `LoadArchive=false` из конфигурации.

Эта сборка сохраняет исходную целевую платформу проекта: .NET 5; runtime в ZIP не включён.

Параметр `JsonFile:Path` заменён на `JsonFile:Folder`. При обновлении старой версии
укажите новую пустую папку и заново конвертируйте журнал. В новой папке сохраняются
`.position.json` и `.export.lock` — не удаляйте их. `.lgx` не обрабатываются; `1Cv8.lgf` обязателен.
