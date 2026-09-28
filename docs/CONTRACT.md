# MCP-2: публичный контракт версии 2

Версия MCP-сервера 2.0.0; схема JSON и контракт bridge остаются версии 1.

| Публичный tool | Аргументы |
|---|---|
| revit-documentation-ping | нет |
| get-documentation-elements | scope="document", sheetIds=null, viewIds=null, documentSession=null, includeTemplates=false |
| create-documentation-elements-report | filePath: string |
| get-documentation | те же аргументы, что у get-documentation-elements |

Старые get-documentation-sheets, get-documentation-views, get-documentation-elements-parameters и create-documentation-report не зарегистрированы, алиасов нет. Точные входные JSON Schema: tool-schemas.json.

## Область и сессия

scope=document запрещает непустые фильтры; sheets требует sheetIds и запрещает непустые viewIds; views — наоборот. ID положительные int64, повторения удаляются. activeView не поддерживается. Без documentSession начальный запрос получает сессию от bridge. Явная сессия проверяется как GUID N и должна совпасть с ответом. Все пакеты параметров используют ту же сессию; перепривязки к другому документу нет.

Выбранные листы включают их виды; выбранные виды — контекст листов без соседних размещений. Весь документ включает неразмещённые виды. Шаблоны по умолчанию исключены. Отбор аннотаций — по OwnerViewId, не по фактической видимости на печати.

## Переиспользование

DocumentationPipeline.GetElementsAsync вызывает прежний DocumentationQueries.Query через CollectAsync: ОДИН collection=viewGraph с fields=[Sheets,Views,Placements,Elements,Relations], includeAnnotations=true. Этот reader уже получает листы, виды, размещения и аннотации. Повторные вызовы SheetsAsync/ViewsAsync не нужны: они заново искали бы полученные данные.

Из проверенного графа извлекаются все уникальные ID массивов sheets/views/placements/elements. Существующий DocumentationParametersExport.ExportAsync получает ID, исходный StructuredContent и сессию. Использует прежний elementParameters, scope=document, последовательные пакеты <=10 и прежний ответ-массив. Параметры типов приходят вместе с параметрами экземпляров. Новый поиск не выполняется. Сохраняется один общий JSON.

Прежние SheetsAsync, ViewsAsync, ElementsAsync, Merge и ExportAsync сохранены. Они не зарегистрированы как MCP tools. CollectAsync разрешает первоначальное получение сессии тем же запросом; старый внутренний ElementsAsync по-прежнему требует сессию.

## Результаты

Каталог JSON/HTML: D:\BIM-S-MCP-2_Отчеты_Версии модели (ReportFiles.DefaultRoot).

get-documentation-elements возвращает status, documentSession, scope, counts, filePath, processedElementCount, requestedElementCount, unprocessedElementIds, errors. create-documentation-elements-report возвращает status, documentSession, scope, counts, filePath, sourceFilePath. get-documentation возвращает результат сбора с jsonPath/htmlPath вместо filePath.

JSON schemaVersion=1: server, snapshotId, startedAtUtc/completedAtUtc, documentSession, scope, document, status=complete|partial, coverage[], sheets[], views[], placements[], elements[], relations[], parameters[], requestedElementIds, unprocessedElementIds, errors[], warnings[], counts. coverage=[elements] — прежнее обозначение полного графа, включая листы и виды, а не ограничение только аннотациями.

Сущность: elementId, uniqueId, class, categoryId/category, name, familyName, typeName/typeId, ownerViewId, kind, properties, status=ok|partial, errors. Свойство: {status:ok|noValue|notApplicable|unsupported|error,value,source,unit?,coordinateSystem?}. Связь: {kind,fromId,toId}; её концы присутствуют в графе. Внешние цели марок/размеров находятся внутри properties. parameters хранит прежние строки bridge с PascalCase ElementId/InstanceParameters/TypeParameters.

HTML использует прежний DocumentationReport.CreateAsync, сохранённый JSON, прежний стиль BIM-S и HTML-кодирование. Совместимые старые JSON schemaVersion=1 также принимаются. Отдельный report tool может показать partial JSON с заметной маркировкой.

get-documentation напрямую вызывает GetElementsAsync → CreateAsync, без MCP loopback. Проверяет полноту, сессию, область, количество ID, пути и sourceFilePath. При partial HTML автоматически не создаётся — по принципу get-model MCP-1. Пустой подтверждённый граф сохраняется в JSON/HTML без запроса пустого пакета параметров.

## Ошибки

Ошибка discovery/полный отказ параметров — без нового JSON. Частичная выгрузка — один partial JSON, IsError=true и filePath; get-documentation добавляет jsonPath и останавливается перед HTML. Ошибка HTML сохраняет доступ к JSON через jsonPath. Отмена до записи не создаёт файл; после JSON не удаляет его.

Ошибки содержат status, stage и доступную диагностику; композиция сохраняет causeStage и jsonPath. Старое имя внутреннего обработчика параметров может остаться в stage — это не публичный tool. Времена отражают интервал чтений, не атомарную ревизию документа. Неизвестный bridge-контракт отклоняется без fallback к 3D.
