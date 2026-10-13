# Инструменты для чтения и экспорта журнала регистрации 1С
Репозиторий содержит библиотеки и инструменты для чтения журнала регистрации 1С и экспорта в файлы JSON Lines, ClickHouse и ElasticSearch. Файловый режим позволяет передать доставку событий в Elasticsearch агенту Filebeat. В основе служб экспорта находится pipeline (TPL Dataflow) обработка данных, за счет чего достигается высокая скорость экспорта с возможностью параметризации потребления ресурсов CPU<->RAM.

## Экспорт в файлы для Filebeat

### Готовая сборка для Windows

В GitHub откройте **Releases → Draft a new release**, создайте новый тег (например,
`v1.0.0-filebeat`) на ветке `master` с текущими изменениями и нажмите **Publish release**.
Workflow **Windows release** проверит экспортер, соберёт компактную Windows x64-версию
и добавит `EventLogExporter-win-x64.zip` в **Assets** этого релиза. Перед первой
публикацией в fork при необходимости включите GitHub Actions во вкладке **Actions**.
ZIP появится после успешного завершения workflow, а не сразу при создании релиза.
SDK на сервере не требуется. Runtime в ZIP не включён: на сервере должен быть установлен
**Microsoft.NETCore.App 5.0.17 x64**. Проверка: `dotnet --list-runtimes`.
Отправка нового тега `v*` также запускает сборку и автоматически публикует релиз с ZIP.

Сборку можно запустить и без релиза: **Actions → Windows release → Run workflow**.
В этом случае ZIP будет доступен в **Artifacts** завершённого запуска.
Распакуйте весь архив на сервере и следуйте [инструкции для Windows](examples/RUN-WINDOWS.md).

### Конфигурация экспорта

Поддерживается старый формат журнала 1С: каталог с `1Cv8.lgf` и файлами `*.lgp`.
В `OneSTools.EventLog.Exporter/appsettings.json` задайте:

```json
"Exporter": {
  "StorageType": 3,
  "LogFolder": "/srv/1c/1Cv8Log",
  "Portion": 10000,
  "TimeZone": "Asia/Yekaterinburg",
  "WritingMaxDegreeOfParallelism": 1,
  "CollectedFactor": 3,
  "ReadingTimeout": 1,
  "LoadArchive": false,
  "SkipEventsBeforeDate": "0001-01-01T00:00:00"
},
"JsonFile": {
  "Path": "/var/log/1c/events.jsonl"
}
```

`StorageType` можно также задать строкой `"JsonFile"`. Укажите часовой пояс исходного
журнала; `DateTime` записывается в UTC. Выходной каталог создаётся автоматически.
Расширение файла произвольное: `.jsonl`, `.json` или `.log`; содержимое всегда JSON Lines,
один JSON-объект на строку, UTF-8 без BOM. Все поля `EventLogItem` сохраняются, включая
имя исходного файла, позиции чтения, пользователя, событие и комментарий. Переносы строк
в комментариях экранируются, поэтому многострочные события остаются одной строкой.
Это поток объектов, а не JSON-массив.

При `LoadArchive: false` экспортер продолжает наблюдать журнал и добавляет новые события.
При перезапуске он восстанавливает позицию из последней полной строки выходного файла.
Незавершённая последняя строка после прерванной записи удаляется, а событие перечитывается
из исходного журнала. Повреждённая полная строка вызывает ошибку. Запись должна идти одним
потоком (`WritingMaxDegreeOfParallelism: 1`), и каждому исходному журналу нужен свой выходной файл.
Файл одновременно доступен Filebeat для чтения. Автоматической ротации нет: не удаляйте,
не обрезайте и не переименовывайте выходной файл во время работы. Для смены выходного файла
требуется остановка и планирование повторного чтения, поскольку позиция хранится в самом файле.
При аварийном завершении возможна повторная доставка событий, уже прочитанных Filebeat.

Для разовой конвертации установите `LoadArchive: true`: весь доступный журнал будет
прочитан с начала, последняя порция дописана, приложение завершится. Повторный запуск
в этом режиме снова добавит весь архив; для нового независимого экспорта используйте новый файл.

Менеджер также поддерживает `StorageType: 3`. Для него вместо `JsonFile:Path` задайте
`JsonFile:Folder`, например `/var/log/1c`. Каждая база получает файл
`<Folder>/<SHA256 абсолютного пути каталога базы>/events.jsonl`, поэтому одинаковые имена
баз в разных кластерах не смешиваются. Параметры `Manager:ClstFolders` заполняются как обычно.

Пример Filebeat: [examples/filebeat.yml](examples/filebeat.yml). Замените пути и адрес ES
на свои, затем проверьте конфигурацию командой `filebeat test config -c examples/filebeat.yml`.
Пример использует [filestream с парсером ndjson](https://www.elastic.co/docs/reference/beats/filebeat/filebeat-input-filestream),
складывает поля события под `ones` и устанавливает `@timestamp` из `ones.DateTime`
через [процессор timestamp](https://www.elastic.co/docs/reference/beats/filebeat/processor-timestamp).
Секции `ClickHouse` и `ElasticSearch` экспортеру в файловом режиме не нужны.

Сборка и проверка (для проверок нужен SDK .NET 10):

```bash
dotnet build OneSTools.EventLog.sln -m:1
dotnet run --project tests/Exporter.SmokeTests
```

Приложения пока нацелены на .NET 5. Если установлен только новый runtime, для запуска
на нём можно явно разрешить переход на старшую версию:

```bash
DOTNET_ROLL_FORWARD=Major dotnet run --project OneSTools.EventLog.Exporter -- --Exporter:StorageType=JsonFile --Exporter:WritingMaxDegreeOfParallelism=1 --Exporter:LogFolder=/srv/1c/1Cv8Log --JsonFile:Path=/var/log/1c/events.jsonl
```

Настройки читаются из текущего рабочего каталога. При запуске из корня репозитория
передайте `--Exporter:StorageType=JsonFile --Exporter:WritingMaxDegreeOfParallelism=1`
или запускайте из каталога `OneSTools.EventLog.Exporter`, где находится `appsettings.json`.
В окружении `Development` локальный `appsettings.Development.json` также переопределяет настройки;
параметры командной строки имеют приоритет.
При запуске старого Host .NET 5 на новом runtime используйте Ctrl+C / SIGINT для штатной
остановки: в проверенной среде SIGTERM завершает процесс без ожидания очереди. После такой
остановки незаписанные события будут перечитаны при следующем запуске, если исходный журнал
сохранён. Для службы Linux с этим вариантом запуска задайте `KillSignal=SIGINT`.

Обзор инструмента и использование в Docker (от [Андрея Овсянкина](https://github.com/EvilBeaver)):  
[Сверхбыстрый Журнал Регистрации 1C с помощью Yandex Clickhouse](https://youtu.be/HnZ0Of-YpW0)

## Состав:
|Наименование|Описание|Actions/Nuget|
|:-----------|:-------|:------------:|
|[OneSTools.EventLog](https://github.com/akpaevj/OneSTools.EventLog/tree/master/OneSTools.EventLog)|Библиотека для чтения журнала регистрации (старый формат, LGF и LGP файлы). Позволяет выполнять как разовое чтение данных, так и запуск в "live" режиме|[![Nuget](https://img.shields.io/nuget/v/OneSTools.EventLog)](https://www.nuget.org/packages/OneSTools.EventLog)  ![EventLog .NET 5](https://github.com/akpaevj/OneSTools.EventLog/workflows/EventLog%20.NET%205/badge.svg)|
|[OneSTools.EventLog.Exporter.Core](https://github.com/akpaevj/OneSTools.EventLog/tree/master/OneSTools.EventLog.Exporter.Core)|Библиотека-ядро для инструментов экспорта журнала регистрации||
|[EventLogExporter](https://github.com/akpaevj/OneSTools.EventLog/tree/master/OneSTools.EventLog.Exporter)|Служба для экспорта журнала регистрации в [ClickHouse](https://clickhouse.tech/) и [ElasticSearch](https://www.elastic.co/)|![EventLogExporter .NET 5](https://github.com/akpaevj/OneSTools.EventLog/workflows/EventLogExporter%20.NET%205/badge.svg)|
|[EventLogExportersManager](https://github.com/akpaevj/OneSTools.EventLog/tree/master/OneSTools.EventLog.Exporter.Manager)|Служба, выполняющая роль менеджера и наблюдающая каталоги серверов на предмет появления/удаления информационных баз с автоматическим подключением/отключением экспорта их журналов регистраций||

## Get started:

Инструмент поставляется с возможностью использования в 2 режимах:  
1. С использованием менеджера экспортеров - это полноценный сервис наблюдения за кластером серверов 1С, автоматизирующий подключение/отключение экспорта ЖР информационных баз **(EventLogExportersManager.exe/dll)**  
2. Без использования менеджера - отдельный процесс, обслуживающий ЖР конкретно указанной информационной базы **(EventLogExporter.exe/dll)**

В случае использования приложения без менеджера, нет необходимости заполнять секцию *Manager* конфигурационного файла  

### Конфигурация:
Файл конфигурации (appsettings.json) разбит на несколько секций, каждая из которых отвечает за функциональность определенной части приложения.

**Manager:**  
Секция настроек менеджера служб экспорта.  
```json
"Manager": {
    "ClstFolders": [
      {
        "Folder": "C:\\\\Program Files\\1cv8\\srvinfo\\reg_1541",
        "Templates": [
          {
            "Mask": "upp_main",
            "Template": "[IBNAME]_el"
          }
        ]
      },
      {
        "Folder": "C:\\\\Program Files\\1cv8\\srvinfo\\reg_1542",
        "Templates": [
          {
            "Mask": "bp3_.*",
            "Template": "[IBNAME]_el"
          }
        ]
      }
    ]
  }
```
где:  
1. *ClstFolders* - массив описаний каталогов рабочих серверов (reg_*)  
2. *Folder* - Путь к каталогу рабочего сервера (reg_*)
3. *Templates* - шаблоны обрабатываемых наименований информационных баз и правила наименования баз данных логов  
  *Mask* - регулярное выражение, применяемое к имени информационной базы  
  *Template* - шаблон имени базы данных хранения логов журнала. Обязательно должен содержать в себе переменную [IBNAME]  
  
Информационные базы, которые не попали ни под одну из масок будут пропущены, экспорт их журналов выполняться не будет  

**При использовании менеджера изменяются настройки для СУБД. При использовании ClickHouse из строки подключения нужно удалить параметр Database, менеджер автоматически создаст базу данных для каждой экспортируемой информационной базы с именем, которое будет определено в зависимости от выбранного шаблона наименования. При использовании ElasticSearch имя индекса будет определено таким же способом, поэтому параметр Index будет просто проигнорирован**

**Exporter:**  
В этой секции размещены общие параметры экспортера, не зависящие от СУБД.
```json
"Exporter": {
    "StorageType": 2,
    "LogFolder": "C:\\Users\\akpaev.e.ENTERPRISE\\Desktop\\1Cv8Log",
    "Portion": 10000,
    "TimeZone": "Europe/Moscow",
    "WritingMaxDegreeOfParallelism": 8,
    "CollectedFactor": 8,
    "ReadingTimeout": 1,
    "LoadArchive": false
  }
```
где:  
1. *StorageType* - тип хранилища журнала регистрации. Может принимать значения:  
*1* - Clickhouse  
*2* - ElasticSearch  
*3* - JsonFile (JSON Lines для Filebeat)
2. *LogFolder* - путь к каталогу журнала регистрации 1С  
3. *Portion* - Размер порции, записываемый в БД за одну итерацию (10000 по умолчанию)  
4. *TimeZone* - часовой пояс (в формате IANA Time Zone Database), в котором записан журнал регистрации. По умолчанию - часовой пояс системы  
5. *WritingMaxDegreeOfParallelism* - количество потоков записи в СУБД. Т.к. в ClickHouse не поддерживаются одновременные BULK операции, то параметр имеет смысл только для ElasticSearch. По умолчанию - 1  
6. *CollectedFactor* - коэффициент количества элементов, которые могут быть помещены в очередь записи. Предельное количество элементов равно Portion * CollectedFactor. По умолчанию - 2  
7. *ReadingTimeout* - таймаут сброса данных при достижении конца файла (в секундах). По умолчанию - 1 сек.  
8. *LoadArchive* - Специальный параметр, предназначенный для первоначальной загрузки архивных данных. При установке параметра в true, отключается "live" режим и не выполняется запрос последнего обработанного файла из БД

**ClickHouse:**
```json
"ConnectionStrings": {
    "Default": "Host=localhost;Port=8123;Username=default;password=;Database=database_name;"
  }
```
**ElasticSearch:**
```json
"ElasticSearch": {
    "Nodes": [
      {
        "Host": "http://192.168.0.95:9200",
        "AuthenticationType": "0"
      },
      {
        "Host": "http://192.168.0.93:9200",
        "AuthenticationType": "1",
        "UserName": "",
        "Password": ""
      }
      {
        "Host": "http://192.168.0.94:9200",
        "AuthenticationType": "2",
        "Id": "",
        "ApiKey": ""
      }
    ],
    "Index": "upp-main-el",
    "Separation": "M",
    "MaximumRetries": 2,
    "MaxRetryTimeout": 30
  }
```
где:</br>
1. *Nodes* - узел, содержащий хосты кластера ElasticSearch, либо один узел при работе с одной нодой. При недоступности текущего узла будет происходить переключение на следующий узел списка. Для узлов доступны 3 типа аутентификации: </br>
*0* - без аутентификации</br>
*1* - Basic</br>
*2* - ApiKey</br>
2. *Index* - префикс названия индекса, конечное название будет определено в зависимости от значения параметра Separation.</br>
3. *Separation* - метод разделения данных по индексам. Может принимать значения:</br>
*H* (Hour) - делить индексы по часам. Пример конечного названия индекса: index-name-el-2020010113</br>
*D* (Day) - делить индексы по дням. Пример конечного названия индекса: index-name-el-20200101</br>
*M* (Month) - делить индексы по месяцам. Пример конечного названия индекса: index-name-el-202001</br>
При указании любого другого (либо не указании вовсе) значения, разделения индекса не будет и конечное название индекса будет выглядеть так: index-name-el-all</br>
4. *MaximumRetries* - количество попыток переподключения к очередному узлу
5. *MaxRetryTimeout* - таймаут попытки подключения

Так-же при первом подключении к узлу приложение проверяет наличие шаблона индекса (Index template) с именем "oneslogs" и при отсутствии - создает. Если шаблон уже создан, то его перезапись происходить не будет, так как предполагается возможная ручная модификация первично созданного шаблона.

**Пример файла кофигурации, содержащий секции для всех поддерживаемых СУБД:**
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "Manager": {
    "ClstFolders": [
      {
        "Folder": "C:\\\\Program Files\\1cv8\\srvinfo\\reg_1541",
        "Templates": [
          {
            "Mask": "upp_main",
            "Template": "[IBNAME]_el"
          }
        ]
      }
    ]
  },
  "Exporter": {
    "StorageType": 2,
    "LogFolder": "C:\\Users\\akpaev.e.ENTERPRISE\\Desktop\\1Cv8Log",
    "Portion": 10000,
    "TimeZone": "Europe/Moscow",
    "WritingMaxDegreeOfParallelism": 1,
    "CollectedFactor": 2,
    "ReadingTimeout": 1,
    "LoadArchive": false
  },
  "ClickHouse": {
    "ConnectionString": "Host=192.168.0.93;Port=8123;Database=upp_main_el;Username=default;password=;"
  },
  "ElasticSearch": {
    "Nodes": [
      {
        "Host": "http://192.168.0.95:9200",
        "AuthenticationType": "0"
      }
    ],
    "Index": "upp-main-el",
    "Separation": "M",
    "MaximumRetries": 2,
    "MaxRetryTimeout": 30
  }
}
```

### Использование:
Все приложения могут быть запущены в 2 режимах: как обычное приложение, либо как служба Windows/Linux. Для теста в Вашей среде, достаточно просто выполнить конфигурацию приложения в файле *appsettings.json*, установить runtime .net 5 (при его отсутствии) и запустить exe/dll. Базы данных в СУБД вручную создавать не нужно, они будут созданы автоматически.

Для запуска приложения как службы необходимо (название службы и путь к исполняемому файлу подставить свои):</br>

**Windows:**</br>
Поместить файлы приложения в каталог и выполнить в консоли команду:
```
sc create EventLogExporter binPath= "C:\elexporter\EventLogExporter.exe"
```
и запустить службу командой:
```
sc start EventLogExporter
```
**Linux: (на примере Ubuntu 20.04.1 LTS)**:</br>
*В этом примере файлы приложения были помещены в каталог /opt/EventLogExporter*</br>
В /etc/systemd/system создать файл eventlogexporter.service с содержимым:
```
[Service]
Type=notify
WorkingDirectory=/opt/EventLogExporter
ExecStart=/usr/bin/dotnet /opt/EventLogExporter/EventLogExporter.dll

[Install]
WantedBy=multi-user.target
```
Применить изменения командой:
``` 
systemctl daemon-reload
```
и запустить службу:
```
systemctl start eventlogexporter.service
```

### Результаты тестирования:
Для теста был использован сервер с Intel Xeon E5-2643 3.40 GHz x2, 128 GB RAM и SAS дисками (Windows Server 2016). Экземпляр ElasticSearch установлен на хосте, экземпляр ClickHouse развернут на нем же в виртуальной машине (Hyper-V) с 4096 MiB RAM. Размер загружаемого журнала регистрации - 945 MiB.</br>

|СУБД         |Порция|Время загрузки  |Потребляемая память  |Событий/сек  |MiB/сек  |Итоговый размер таблицы|
|:-----------:|:----:|:--------------:|:-------------------:|:-----------:|:-------:|:---------------------:|
|ClickHouse   |10000 |1 мин. 41 сек.  | ~ 60 MiB            |71032        |9.13     |56.66 MiB              |
|ElasticSearch|5000  |2 мин. 35 сек.  | ~ 100 MiB           |45968        |6.09     |1106.7 MiB             |

ClickHouse использовался as is, но к колонкам (в зависимости от типа и состава данных) применены кодеки. Для шаблона индекса ElasticSearch были выставлены параметры number_of_shards = 6, number_of_replicas = 0, index.codec = best_compression и использовалось 4 потока записи.
