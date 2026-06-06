# AGENTS

Инструкция для написания Revit-плагинов в проектах этого стека.

Документ задаёт практические правила для:

- регистрации команд через `PluginsManager`
- логирования через `RevitLogger`
- простого и поддерживаемого устройства кода
- работы с WinForms
- работы с Excel через `EPPlus 4.5.3.3`
- исследования и обхода ограничений Revit API через рефлексию

## Базовый стек

Для всех новых Revit-плагинов по умолчанию использовать:

- `.NET Framework 4.8`
- `Autodesk.Revit.DB`
- `Autodesk.Revit.UI`
- `RevitLogger`
- `EPPlus 4.5.3.3`
- `Windows Forms` для простых UI
- `PluginsManager` как основной способ запуска команд

Если нет явной причины делать иначе, придерживаться именно этого стека.

## PluginsManager

### Общий принцип

Плагины запускаются через `PluginsManager`, который подгружает команды из внешних DLL.

Предпочтительный способ регистрации команды в `PluginsManager`:

- добавить специальные статические поля прямо в класс команды, реализующий `IExternalCommand`

Обязательные поля:

```csharp
public static string IS_TAB_NAME => "Название вкладки";
public static string IS_NAME => "Название команды";
public static string IS_IMAGE => "ИмяПроекта.Resources.icon.png";
public static string IS_DESCRIPTION => "Описание команды";
```

Назначение полей:

- `IS_TAB_NAME` — имя вкладки в `PluginsManager`
- `IS_NAME` — отображаемое имя команды
- `IS_IMAGE` — путь к embedded resource-изображению
- `IS_DESCRIPTION` — описание команды для пользователя

### Правила оформления команд для PluginsManager

- Каждая пользовательская команда должна быть отдельным классом с `IExternalCommand`
- У каждой команды должны быть заполнены все 4 поля `IS_*`
- Текст `IS_NAME` должен быть коротким и понятным пользователю
- Текст `IS_DESCRIPTION` должен объяснять назначение команды и, при необходимости, содержать автора или ссылку на проект
- Картинка должна быть добавлена в ресурсы проекта с типом `Embedded Resource`

Пример:

```csharp
[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
public class MyCommand : IExternalCommand
{
    public static string IS_TAB_NAME => "ISTools";
    public static string IS_NAME => "Экспорт IFC";
    public static string IS_IMAGE => "MyPlugin.Resources.ifc_export.png";
    public static string IS_DESCRIPTION => "Экспорт модели в IFC с дополнительной настройкой";

    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        return Result.Succeeded;
    }
}
```

### Альтернативная регистрация через XML

Если нельзя менять исходный код команды, `PluginsManager` умеет читать `commands_config.xml`.

Основные поля XML:

- `CmdCode` — полное имя класса команды, например `MyPlugin.ExportCommand`
- `CmdTab` — вкладка
- `CmdName` — отображаемое имя
- `CmdDescription` — описание
- `CmdImage` — имя картинки

Но если есть доступ к коду плагина, основной и предпочтительный способ — именно поля `IS_*` в классе команды.

## Логирование

### Обязательная библиотека

Для логирования использовать `RevitLogger`.

Не писать собственный `Logger`, если для этого нет очень веской причины.

Использовать:

- `RevitLogger.Logger` — файловый лог
- `RevitLogger.DebugWindow` — простое окно быстрой диагностики

### Цели логирования

Логирование должно быть подробным и пригодным для отладки чужим человеком.

Лог должен помогать ответить на вопросы:

- что именно запускалось
- с какими входными данными
- на каком шаге произошла ошибка
- какое было состояние документа, путей, конфигурации и промежуточных результатов
- почему было принято то или иное решение

Плохой лог:

```csharp
Logger.Info("Старт");
```

Хороший лог:

```csharp
Logger.Info($"Запуск экспорта | File={filePath} | View={viewName} | HasActiveDocument={doc != null}");
```

### Обязательный сценарий логирования в `Execute`

В начале команды:

1. задать путь к лог-файлу
2. задать уровень логирования
3. вызвать `Logger.Init(...)`
4. записать старт команды

Минимальный рекомендуемый шаблон:

```csharp
using RevitLogger;

[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
public class MyCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        Logger.SetLogPath(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp",
                "MyPlugin",
                "plugin.log"));

        Logger.SetLogLevel(Logger.LogLevel.Debug);
        Logger.Init(
            hostName: "Autodesk Revit",
            hostVersionNumber: commandData.Application.Application.VersionNumber,
            hostBuild: commandData.Application.Application.VersionBuild,
            hasActiveDocument: commandData.Application.ActiveUIDocument != null);

        try
        {
            Logger.Info("[MyCommand] Старт команды");

            var uidoc = commandData.Application.ActiveUIDocument;
            var doc = uidoc?.Document;

            Logger.Debug($"[MyCommand] ActiveDocument={doc?.Title ?? "null"}");

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Logger.Exception(ex, "[MyCommand] Ошибка выполнения команды");
            message = ex.Message;
            return Result.Failed;
        }
    }
}
```

### Что логировать обязательно

Обязательно логировать:

- вход в публичные методы
- ключевые параметры метода
- найденные пути к файлам
- результаты поиска элементов, видов, семейств, листов, параметров
- количество найденных элементов
- условия пропуска
- значения, влияющие на ветвление логики
- начало и конец долгих операций
- создание временных файлов
- экспорт, импорт, чтение, запись
- открытие и закрытие документов
- обработку исключений

### Какие уровни использовать

- `Logger.Debug(...)` — подробная техническая диагностика
- `Logger.Info(...)` — основные этапы сценария
- `Logger.Warning(...)` — нештатное, но допустимое поведение
- `Logger.Error(...)` — ошибка операции, после которой текущий шаг не выполнен
- `Logger.Critical(...)` — критическая ошибка всей команды
- `Logger.Exception(...)` — запись исключения с полным контекстом

### Правила формулировки сообщений

- В начале строки указывать контекст: `[ИмяКласса]`
- Сообщение должно содержать данные, а не только эмоцию
- Не писать абстрактные фразы вроде `ошибка`, `не получилось`, `что-то пошло не так`
- Если операция пропущена, писать причину пропуска
- Если найден объект, писать его ключевые свойства

Примеры:

```csharp
Logger.Info("[ExportCommand] Старт пакетного экспорта");
Logger.Debug($"[ExportCommand] Найдено моделей: {models.Count}");
Logger.Warning($"[ExportCommand] Вид '{viewName}' не найден, используется fallback '{fallbackView}'");
Logger.Error($"[ExportCommand] Файл конфигурации не найден: {configPath}");
Logger.Exception(ex, $"[ExportCommand] Ошибка экспорта файла: {fileName}");
```

### DebugWindow

`DebugWindow` использовать только для быстрой визуальной диагностики, когда нужно:

- показать промежуточные строки пользователю или разработчику
- быстро проверить последовательность обработки
- вывести короткий итог

Пример:

```csharp
DebugWindow.AddRow("Старт обработки");
DebugWindow.AddRow($"Найдено элементов: {elements.Count}");
DebugWindow.AddRow($"Текущий документ: {doc?.Title}");
DebugWindow.Show("Отладка команды");
```

`DebugWindow` не заменяет файловый лог.

## Стиль кода

### Общий принцип

Код должен быть:

- понятным
- задокументированным
- простым в сопровождении
- предсказуемым для следующего разработчика

### Документирование

Для классов, публичных методов, свойств и нестандартных приватных методов использовать XML-документацию в стиле C#:

```csharp
/// <summary>
/// Открывает документ Revit с преднастройкой рабочих наборов.
/// </summary>
/// <param name="modelPath">Путь к модели.</param>
/// <param name="excludePattern">Подстрока для исключения рабочих наборов.</param>
/// <returns>Открытый документ или null, если открыть документ не удалось.</returns>
private Document OpenDocument(string modelPath, string excludePattern)
{
    ...
}
```

Дополнительно:

- ставить краткие inline-комментарии перед нетривиальными блоками
- не комментировать очевидное
- не оставлять “временные” комментарии без смысла

Плохой комментарий:

```csharp
// Устанавливаем значение
count = 5;
```

Хороший комментарий:

```csharp
// В Revit сначала открываем документ с закрытыми workset'ами, чтобы
// определить состав наборов без лишней загрузки модели в память.
```

### Именование

- Имена классов, методов и свойств — осмысленные, без сокращений “на глаз”
- Имя должно отражать действие или сущность
- Не использовать бессмысленные имена вроде `DoWork`, `Helper`, `Data1`, `Temp2`

## Архитектура

### Общий принцип

Предпочтение отдаётся простой архитектуре.

Если задача маленькая:

- использовать один класс
- разбивать логику на маленькие методы
- не вводить лишние слои

Не усложнять структуру без необходимости.

### Что считается хорошим подходом

- одна команда = один класс команды
- если UI маленький, форма = один класс формы
- основная логика разбита на небольшие методы по 10-40 строк
- методы делают одну понятную задачу
- зависимостей между классами немного

### Что нежелательно без веской причины

- искусственные `Service`, `Repository`, `Factory`, `Manager` для маленького плагина
- глубокая иерархия классов
- лишние интерфейсы ради “красивой архитектуры”
- избыточный DI-контейнер
- большое количество абстракций поверх простого Revit API

### Практическое правило

Сначала писать простое решение.

Переходить к дополнительным слоям только если:

- логика реально стала большой
- есть несколько независимых сценариев
- повторение кода начало мешать
- нужно поддерживать разные версии API или разные источники данных

## Формы и UI

### Общий принцип

Для простого и быстрого создания форм использовать `Windows Forms`.

Формы писать вручную прямо в одном классе.

Не использовать:

- `Designer.cs`
- отдельный `.resx` для простой формы
- раздробленный код формы на много файлов, если в этом нет необходимости

### Как именно оформлять форму

Ориентир — подход из `ExportConfigForm.cs` в текущем проекте:

- один класс, наследующий `Form`
- публичные свойства для контролов, с которыми будет работать команда
- конструктор, который вызывает методы сборки UI
- отдельный метод `SetupForm()` для общих параметров окна
- отдельный метод `SetupControls()` для создания и компоновки контролов
- простые методы доступа к данным формы

Рекомендуемая структура:

```csharp
public class MyForm : Form
{
    public Button BtnRun { get; private set; }
    public TextBox TxtPath { get; private set; }
    public Label LblStatus { get; private set; }

    public MyForm()
    {
        SetupForm();
        SetupControls();
    }

    private void SetupForm()
    {
        ...
    }

    private void SetupControls()
    {
        ...
    }

    public void SetStatus(string message)
    {
        ...
    }
}
```

### Правила для простых WinForms

- Не делать визуально сложный UI без необходимости
- Использовать стандартные контролы: `Button`, `TextBox`, `Label`, `TableLayoutPanel`, `FlowLayoutPanel`
- Делать форму читаемой и аккуратно собранной кодом
- Минимизировать код в обработчиках UI-событий
- Долгую логику выносить в отдельные методы команды или формы

## Excel

Для работы с Excel использовать только `EPPlus 4.5.3.3`.

Правила:

- не менять версию без явной необходимости
- при работе с листами помнить про особенности версии 4.x
- логировать загрузку файла, листа, количества строк и ключевых значений

Если создаётся шаблон Excel:

- задавать понятные заголовки
- делать минимальное форматирование
- добавлять пример строки, если это помогает пользователю

## Revit API и рефлексия

### Когда использовать рефлексию

Если есть сомнения по Revit API, можно и нужно использовать рефлексию для изучения:

- `RevitAPI.dll`
- `RevitAPIUI.dll`
- `Autodesk.IFC.Export.UI.dll`
- других связанных DLL

Рефлексия допустима в двух случаях:

- как способ изучения API и сигнатур
- как способ обойти ограничения, различия версий или недоступность нужного вызова напрямую

### Когда это особенно полезно

- метод отличается между версиями Revit
- у типа изменились свойства или сигнатуры
- документация противоречива
- нужно проверить наличие метода перед вызовом
- нужно работать с IFC Exporter API, которое отличается между версиями

### Правила использования рефлексии

- сначала пробовать прямой API, если он стабилен и ясен
- если используется рефлексия, обязательно документировать зачем
- обязательно логировать найденные типы, методы и причины fallback
- оборачивать рефлексию в маленькие изолированные методы

Пример подхода:

```csharp
/// <summary>
/// Пытается найти и вызвать метод UpdateOptions в зависимости от версии IFC Exporter.
/// </summary>
private void TryUpdateOptions(Type configType, object config, IFCExportOptions options, ElementId viewId, Document document)
{
    var newMethod = configType.GetMethod(
        "UpdateOptions",
        new[] { typeof(Document), typeof(IFCExportOptions), typeof(ElementId), typeof(bool) });

    if (newMethod != null)
    {
        Logger.Debug("[IfcExportConfig] Используется UpdateOptions(Document, ...)");
        newMethod.Invoke(config, new object[] { document, options, viewId, false });
        return;
    }

    var legacyMethod = configType.GetMethod(
        "UpdateOptions",
        new[] { typeof(IFCExportOptions), typeof(ElementId) });

    if (legacyMethod != null)
    {
        Logger.Debug("[IfcExportConfig] Используется legacy UpdateOptions(...)");
        legacyMethod.Invoke(config, new object[] { options, viewId });
    }
}
```

## Рекомендуемый шаблон команды

Каждая команда должна стремиться к такой структуре:

1. регистрационные поля `IS_*`
2. `Execute(...)`
3. маленькие приватные методы под отдельные шаги
4. подробное логирование
5. обработка исключений через `Logger.Exception(...)`

Каркас:

```csharp
[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
public class SampleCommand : IExternalCommand
{
    public static string IS_TAB_NAME => "ISTools";
    public static string IS_NAME => "Пример команды";
    public static string IS_IMAGE => "SamplePlugin.Resources.sample.png";
    public static string IS_DESCRIPTION => "Пример команды для PluginsManager";

    /// <summary>
    /// Точка входа команды Revit.
    /// </summary>
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        ConfigureLogging(commandData);

        try
        {
            Logger.Info("[SampleCommand] Старт команды");
            Run(commandData);
            Logger.Info("[SampleCommand] Команда завершена успешно");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Logger.Exception(ex, "[SampleCommand] Ошибка выполнения команды");
            message = ex.Message;
            return Result.Failed;
        }
    }

    /// <summary>
    /// Настраивает файловый лог и снимок окружения.
    /// </summary>
    private void ConfigureLogging(ExternalCommandData commandData)
    {
        Logger.SetLogPath(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp",
                "SamplePlugin",
                "plugin.log"));

        Logger.SetLogLevel(Logger.LogLevel.Debug);
        Logger.Init(
            hostName: "Autodesk Revit",
            hostVersionNumber: commandData.Application.Application.VersionNumber,
            hostBuild: commandData.Application.Application.VersionBuild,
            hasActiveDocument: commandData.Application.ActiveUIDocument != null);
    }

    /// <summary>
    /// Выполняет основную логику команды.
    /// </summary>
    private void Run(ExternalCommandData commandData)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        Logger.Debug($"[SampleCommand] ActiveDocument={doc?.Title ?? "null"}");
    }
}
```

## Чеклист перед завершением

- У команды есть поля `IS_TAB_NAME`, `IS_NAME`, `IS_IMAGE`, `IS_DESCRIPTION`
- Картинка подключена как `Embedded Resource`
- Логирование настроено через `RevitLogger`
- В начале команды вызывается `Logger.Init(...)`
- Лог содержит достаточно данных для отладки
- Исключения пишутся через `Logger.Exception(...)`
- Код разбит на маленькие понятные методы
- Нет лишних абстракций для маленькой задачи
- Если есть форма, она написана вручную в одном классе
- Для Excel используется `EPPlus 4.5.3.3`
- Если использовалась рефлексия, она локализована и задокументирована
- Публичные методы и нестандартные участки кода документированы XML-комментариями

## Приоритет решений

Если есть выбор между:

- “архитектурно красиво, но сложно”
- “прямо, просто и поддерживаемо”

по умолчанию выбирать:

- “прямо, просто и поддерживаемо”

Главная цель — чтобы плагин было легко читать, отлаживать и поддерживать через несколько месяцев, даже если его откроет другой разработчик.
