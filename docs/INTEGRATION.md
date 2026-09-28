# Generic bridge и BimSAgent

При объединении публичных tools в версии 2.0.0 bridge, RevitAddin и MCP-1 не изменялись; повторная сборка add-in не требовалась. Ниже описано расширение первоначальной реализации. Новая композиция использует прежний viewGraph один раз и elementParameters пакетами, без новых команд bridge.

Проверены исходники:

- `C:\Users\Anastasia\OneDrive\Desktop\BimSAgent\McpCommands.cs`
- `C:\Users\Anastasia\OneDrive\Desktop\BimSAgent\RevitAddin\NamedPipeBridge.cs`
- `C:\Users\Anastasia\OneDrive\Desktop\BimSAgent\RevitAddin\RevitReadHandler.cs`
- `RevitAddin\BimS.Revit2024.csproj`, `Application.cs`.

## Что расширено

В `RevitReadHandler` добавлена только отдельная ветка `collection=viewGraph`: разбор ViewGraphRequest, сохранение запроса в существующий pending-контекст и вызов reader после существующей проверки documentSession. Прежний разбор elements/document/elementParameters и чтение их ответов сохранены. Имена MCP tools не передаются bridge.

Новые файлы `ViewGraphRequest.cs` и `ViewGraphReader.cs` содержат whitelist полей/фильтров и типизированное чтение Revit API. Нет reflection, выполнения кода, транзакций записи или создания отчётов внутри add-in. SDK-проект add-in автоматически включает новые .cs.

В `bridge/` хранится проверяемая копия расширенных исходников. Для переноса в исходный add-in предусмотрен `scripts/Apply-BridgeSources.ps1`: проверяет SHA256 исходного handler, отказывается перезаписать неожиданную версию или уже существующие новые файлы, сохраняет исходный handler в `artifacts/bridge-backup`, копирует только три изменённых файла. `NamedPipeBridge.cs`, `Application.cs`, BimSAgent и MCP-1 не заменяются.

Перенос уже выполнен; исходный `BimS.Revit2024.csproj` собран успешно, 0 ошибок и 0 предупреждений. Резервная копия handler: `artifacts/bridge-backup/RevitReadHandler.original.cs`. Развёртывание полученной DLL в фактическое расположение `.addin` и перезапуск Revit — отдельный шаг; автоматически не выполняется. Не копируйте `Bridge.Compile.dll` в add-in.

Проверенный исходный `RevitAddin/BIM-S.addin` указывает на `RevitAddin/bin/Release/net48/BimS.Revit2024.dll`. В этой задаче полный add-in собирался в **Debug**, поэтому Release DLL по этому пути не заменялась. Для последующей активации сверить установленный `.addin`, закрыть Revit, собрать исходный add-in с `-c Release` и открыть Revit снова. Это инструкция следующего шага, не выполненное развёртывание.

## Транспорт и многосерверность

Существующий pipe принимает одну строку UTF-8, один ответ на соединение; вход <65536 символов. MCP-2 ограничивает запросы <60000. Pipe защищён SID текущего пользователя, один экземпляр listener, обработка последовательная. Reader использует один pending-запрос под lock и ExternalEvent; при занятости отвечает ошибкой. Это не полноценная очередь параллельных Revit API операций. Два MCP-клиента подключаются по очереди; Revit API работает только в Execute. Длительный запрос одного клиента может привести к тайм-ауту другого, поэтому автоматических параллельных batch-запросов нет.

Сессия выдаётся на открытый Document и проверяется против активного документа непосредственно в Execute. При смене документа MCP-2 получает ошибку, не новую сессию. Ping не проверяет открытый документ или поддержку viewGraph: это только доступность pipe.

## Текущее подключение BimSAgent

JSON-конфигурации списка MCP-серверов в проверенном коде нет. В `McpCommands.cs` жёстко заданы:

- `ServerDirectory = D:\BIM-S-MCP-1\BIM-S_MCP-Server`;
- `StdioClientTransportOptions.Name = BIM-S_MCP-Server`;
- `Command = dotnet`;
- аргумент — `bin\Debug\net10.0\BIM-S_MCP-Server.dll`;
- новый MCP-процесс на CLI-команду;
- клиентский кэш только для MCP-1; общий тайм-аут 30 секунд, 15 минут для текущих model export/pipeline;
- ограниченный набор наследуемых переменных окружения.

Сейчас MCP-2 автоматически в BimSAgent не зарегистрирован. Будущая отдельная запись StdioClientTransportOptions должна иметь:

```csharp
Name = "BIM-S_MCP-Server-2",
Command = "dotnet",
Arguments = [@"D:\BIM-S-MCP-2\BIM-S_MCP-Server-2\bin\Debug\net10.0\BIM-S_MCP-Server-2.dll"],
WorkingDirectory = @"D:\BIM-S-MCP-2\BIM-S_MCP-Server-2"
```

Это параметры подтверждённого SDK transport, а не вымышленная конфигурация BimSAgent. При будущей интеграции добавить выбор сервера, маршрутизацию четырёх tools и timeout (для get-documentation-elements/get-documentation ориентир 15 минут). Сохранить env allowlist. Передавать scope/фильтры и при необходимости documentSession; discoveryResults и ID между этапами теперь передаёт сам MCP-2. Для отдельного HTML передавать filePath ответа get-documentation-elements. Не использовать lastElements MCP-1. Имена tools не пересекаются, оба сервера используют тот же pipe.

## Проверки и незавершённая проверка в Revit

После изменения публичного интерфейса: сборка MCP-2 без ошибок/предупреждений, 58 проверок пройдены. Существующая DLL MCP-1 прошла initialize/tools/list и отклонение некорректной сессии в tool параметров. SHA256 всех 15 исходных/проектных файлов MCP-1 и RevitAddin, зафиксированных перед изменениями, совпал после них. Это проверка неизменности и запуска, не повторная выгрузка живой 3D-модели.

Сборка MCP-2 и расширения bridge успешна. Тесты проверяют контракт, файлы и MCP stdio. Diff handler показывает изолированную ветку новой коллекции. Живой старый bridge проверен: ping, session, отклонение чужой сессии. Новая коллекция отклоняется загруженной прежней версией, что ожидаемо до обновления add-in.

После загрузки обновлённой DLL проверить на документе Revit: лист без viewport, viewport и schedule placements, легенду с повторным размещением, неразмещённый вид, тексты/размеры/марки, удаление элемента между этапами, смену документа, одновременные чтения MCP-1/MCP-2. Регрессию старых коллекций нужно повторить именно на новой загруженной DLL; сборка и сравнение исходников не заменяют эту проверку.
