# Чтение данных Revit 2024

Источник проверки API: установленные `D:\Useful Programs\Revit 2024\RevitAPI.dll` и `RevitAPI.xml`. Доступность методов подтверждена сборкой против DLL; фактические значения и применимость в конкретном проекте требуют проверки в Revit. XML содержит как минимум одно свойство, отсутствующее в DLL (`SpatialElementTag.SpatialElement`); вместо него используются конкретные RoomTag / AreaTag / SpaceTag.

| Сущность | Класс / категория | Что читается | API / ограничение |
|---|---|---|---|
| Лист | ViewSheet | Номер, название, placeholder, outline | SheetNumber, IsPlaceholder, View.Outline; ft, координаты листа |
| Основная надпись | FamilyInstance / OST_TitleBlocks | Семейство, тип, положение, поворот, bounding box | OwnerViewId, LocationPoint; включается и при структурном discovery |
| Вид | View кроме листов и browser views | Тип, масштаб, шаблон, основной/зависимые виды, crop | ViewType, Scale, ViewTemplateId, GetPrimaryViewId, GetDependentViewIds; crop не запрашивается для шаблонов, спецификаций, легенд и drafting views |
| Размещение вида | Viewport | Лист/вид, центр, outline, поворот, номер детали, подпись | SheetId, ViewId, GetBoxCenter/GetBoxOutline, Rotation, VIEWPORT_DETAIL_NUMBER, LabelOffset/LabelLineLength |
| Спецификация на листе | ScheduleSheetInstance | Лист/спецификация, точка, угол, сегмент, признак revision schedule, box | OwnerViewId, ScheduleId, Point, Rotation, SegmentIndex, IsTitleblockRevisionSchedule; отдельная сущность, не viewport |
| Размер | Dimension (включая наследников) | Вид/тип/shape/style, значение и строка, override/prefix/suffix/above/below, сегменты, текстовая позиция, curve и references | Value не подменяется ValueString; сегмент имеет index, не ElementId; неприменимые API дают явную диагностику |
| Марка | IndependentTag | Текст, head, orphaned, все цели, выноски | GetTaggedElementIds/GetTaggedReferences; attached leader end — notApplicable; ссылочные цели не выгружают 3D-геометрию |
| Пространственные марки | SpatialElementTag, RoomTag, AreaTag, Mechanical.SpaceTag | Текст, head, orphaned, link flag, цели и выноски | RoomTag.TaggedRoomId; AreaTag.Area / SpaceTag.Space для локальных целей; linked non-room target — unsupported |
| Текст | TextNote / TextElement | Текст с переносами, положение, ширина, выравнивание, базис, угол в плоскости вида, выноски, сводное форматирование | Text, Coord, Width (бумажные ft), BaseDirection/UpDirection, GetLeaders, GetFormattedText; mixed formatting сохраняется как Mixed, посимвольные runs — unsupported |
| Линия детализации | DetailCurve | Геометрия, стиль | GeometryCurve, LineStyle; ModelCurve не собирается |
| Узел | FamilyInstance / OST_DetailComponents | Тип/семейство, положение, поворот, box | Отбор по OwnerViewId |
| Условное обозначение | FamilyInstance / OST_GenericAnnotation | Тип/семейство, положение, поворот, box | Не все CategoryType.Annotation включаются автоматически |
| Заполненная/маскирующая область | FilledRegion | IsMasking, границы | GetBoundaries, кривые сериализуются, не Revit API objects |
| Облако изменений | RevisionCloud | RevisionId, sketch curves | GetSketchCurves |
| Группа детализации | Group / OST_IOSDetailGroups | MemberIds, groupMember для присутствующих в графе участников | GetMemberIds; исходные MemberIds сохраняют также ссылки вне выбранного графа |
| Параметры | Любая выбранная сущность и её тип | BuiltInParameter метаданные и значения | Прежний elementParameters reader; negative parameterId допустим; source instance/type, ownerElementId проверяются |

Общие поля: ID/UniqueId, класс, категория (числовой ID и имя), Name, FamilyName, TypeName/TypeId, OwnerViewId. Категории не определяются по локализованному имени. У отсутствующих типа/семейства — null без ошибки. Отдельного поиска всех типов нет: их параметры приходят через существующий reader.

`properties.<key>`: статус, значение, источник; при геометрии — единицы и система координат. BoundingBoxXYZ сохраняется с Transform. Кривые сохраняют тип, конечные точки и tessellation для ограниченных кривых; это приближённая сериализация, не точный графический экспорт. Размеры сохраняют внутренние значения Revit с указанием styleType: длина ft, угол rad. На листе координаты sheet; в обычном виде — model; crop содержит локальный box и преобразование в модель; направление текста — безразмерный вектор, угол — относительно базиса вида.

`unsupported` означает намеренно отсутствующую возможность; `notApplicable` — неприменимое свойство; `noValue` — API вернул null; `error` сопровождается ошибкой и переводит сущность/граф в partial. Неизвестные классы не выдаются за поддерживаемые. Полный набор текстовых runs, изображений, imported CAD, всех специфических семейств символов и расчёт фактической видимости не реализованы в v1.

Владение не равно видимости. При зависимых видах аннотации основного вида не дублируются как якобы видимые; IDs связей primary/dependent сохраняются. Применение view filters, crop, hidden elements и печатной видимости не моделируется.
