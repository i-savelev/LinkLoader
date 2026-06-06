using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DebugWindow = RevitLogger.DebugWindow;
using Logger = RevitLogger.Logger;

namespace LinkLoader
{
    /// <summary>
    /// Команда для связывания моделей с Revit Server с автоматическим распределением по рабочим наборам через Excel и закреплением.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class LoadModelsBySharedCoordsCommand : IExternalCommand
    {
        public static string IS_TAB_NAME => "ISTools";
        public static string IS_NAME => "Пакетная подгрузка связей";
        public static string IS_IMAGE => "LinkLoader.Resources.LinkLoader.png";
        public static string IS_DESCRIPTION => "Автор: https://github.com/i-savelev\r\nКоманда загружает указанные модели из ревит-сервера как связи. Для распределения по рабочим наборам используется конфигурация из excel.";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;

            try
            {
                var form = new RevitServerBrowser.RevitServerBrowserForm(commandData);
                Logger.SetLogPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "i-savelev", "LinkLoader.log"));
                Logger.Init(hostName: "Autodesk Revit",
                    hostVersionNumber: commandData.Application.Application.VersionNumber,
                    hostBuild: commandData.Application.Application.VersionBuild,
                    hasActiveDocument: commandData.Application.ActiveUIDocument != null);
                Logger.Info("🚀 === Запуск LoadModelsBySharedCoordsCommand ===");

                if (uiApp.ActiveUIDocument == null || uiApp.ActiveUIDocument.Document == null)
                {
                    string errMsg = "Для связывания моделей необходим открытый активный документ (хост).";
                    Logger.Error($"❌ {errMsg}");
                    MessageBox.Show(errMsg, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return Result.Cancelled;
                }

                Document hostDoc = uiApp.ActiveUIDocument.Document;
                Logger.Info($"✅ Активный документ-хост: {hostDoc.Title}");

                List<(string Pattern, string WorksetName)> worksetMapping = new List<(string, string)>();

                form.ConfirmButton.Click += (s, e) =>
                {
                    try
                    {
                        var selectedPaths = form.SelectedModelPaths?.ToList() ?? new List<string>();
                        Logger.Info($"📋 Выбрано моделей для обработки: {selectedPaths.Count}");

                        if (!selectedPaths.Any())
                        {
                            Logger.Warning("⚠️ Пользователь не выбрал ни одной модели.");
                            MessageBox.Show(form, "Выберите хотя бы одну модель для загрузки",
                                "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        DialogResult useExcelResult = MessageBox.Show(
                            form,
                            "Использовать конфигурацию рабочих наборов из Excel?\n\n" +
                            "• Столбец A: Часть названия файла (шаблон)\n" +
                            "• Столбец B: Целевой рабочий набор\n\n" +
                            "«Нет» — загрузка в рабочий набор по умолчанию.",
                            "Конфигурация рабочих наборов",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);

                        if (useExcelResult == DialogResult.Yes)
                        {
                            using (var dlgConfig = new OpenFileDialog
                            {
                                Title = "📋 Выберите файл конфигурации Excel (.xlsx)",
                                Filter = "Excel Files|*.xlsx;*.xls|All Files|*.*",
                                CheckFileExists = true
                            })
                            {
                                if (dlgConfig.ShowDialog(form) == DialogResult.OK)
                                {
                                    worksetMapping = ParseWorksetExcel(dlgConfig.FileName);
                                    if (worksetMapping.Any())
                                    {
                                        Logger.Info($"✅ Загружено {worksetMapping.Count} правил из Excel.");
                                    }
                                    else
                                    {
                                        Logger.Warning("⚠️ Файл Excel прочитан, но правила не найдены.");
                                    }
                                }
                                else
                                {
                                    Logger.Info("⛔ Выбор файла Excel отменен. Будет использован рабочий набор по умолчанию.");
                                }
                            }
                        }

                        DialogResult confirmResult = MessageBox.Show(
                            form,
                            $"Будут загружены ТОЛЬКО модели с настроенными общими координатами.\n\n" +
                            $"Выбрано моделей: {selectedPaths.Count}\n" +
                            $"Правил в Excel: {worksetMapping.Count}\n" +
                            $"Хост: {hostDoc.Title}\n\n" +
                            $"Модели будут автоматически ЗАКРЕПЛЕНЫ.\n\n" +
                            $"Продолжить?",
                            "Подтверждение связывания",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);

                        if (confirmResult != DialogResult.Yes)
                        {
                            Logger.Info("⛔ Операция отменена пользователем.");
                            return;
                        }

                        ProcessLinking(hostDoc, selectedPaths, worksetMapping);
                    }
                    catch (Exception ex)
                    {
                        Logger.Critical($"❌ [ConfirmButton Click] Критическая ошибка: {ex}");
                        DebugWindow.AddRow($"ERROR: {ex.Message}");
                        DebugWindow.Show();
                    }
                };

                Logger.Info("🔄 Ожидание выбора пользователя в форме...");
                form.ShowDialog();

                Logger.Info("🏁 === Завершение команды ===");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                Logger.Critical($"❌ [CMD Execute] Глобальная ошибка: {ex}");
                DebugWindow.AddRow($"ERROR: {ex.Message}");
                DebugWindow.Show();
                return Result.Failed;
            }
        }

        private List<(string Pattern, string WorksetName)> ParseWorksetExcel(string filePath)
        {
            var mapping = new List<(string, string)>();
            try
            {
                var fileInfo = new FileInfo(filePath);
                using (var package = new ExcelPackage(fileInfo))
                {
                    var worksheet = package.Workbook.Worksheets.FirstOrDefault();

                    if (worksheet == null)
                    {
                        Logger.Warning("⚠️ Excel-файл не содержит рабочих листов.");
                        return mapping;
                    }

                    if (worksheet.Dimension == null)
                    {
                        Logger.Warning("⚠️ Excel-файл пуст или не имеет размерности.");
                        return mapping;
                    }

                    int rowCount = worksheet.Dimension.End.Row;
                    Logger.Info($"📊 Чтение Excel: найдено строк {rowCount} на листе '{worksheet.Name}'");

                    for (int row = 2; row <= rowCount; row++)
                    {
                        string pattern = worksheet.Cells[row, 1].Text?.Trim();
                        string worksetName = worksheet.Cells[row, 2].Text?.Trim();

                        if (!string.IsNullOrWhiteSpace(pattern) && !string.IsNullOrWhiteSpace(worksetName))
                        {
                            mapping.Add((pattern, worksetName));
                            Logger.Info($"   📌 Правило: '{pattern}' -> '{worksetName}'");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"❌ Ошибка чтения Excel-файла: {ex.Message}");
            }
            return mapping;
        }

        private void ProcessLinking(Document hostDoc, IEnumerable<string> modelPaths, List<(string Pattern, string WorksetName)> worksetMapping)
        {
            Logger.Info("⚙️ === Начало процесса связывания моделей ===");

            int successCount = 0;
            int skipCount = 0;
            int failCount = 0;

            foreach (string userVisiblePath in modelPaths)
            {
                try
                {
                    Logger.Info($"▶ Обработка пути: {userVisiblePath}");

                    ModelPath modelPath;
                    try
                    {
                        modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(userVisiblePath);
                        string roundTripPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
                        if (string.IsNullOrEmpty(roundTripPath))
                        {
                            Logger.Error($"   ❌ Ошибка: ModelPath создан, но невалиден.");
                            failCount++;
                            continue;
                        }
                        Logger.Info($"   ✅ Путь успешно преобразован в ModelPath.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"   ❌ Ошибка преобразования пути: {ex.Message}");
                        failCount++;
                        continue;
                    }

                    string fileName = Path.GetFileName(userVisiblePath);
                    string transactionName = $"Проверка и связь: {fileName}";

                    using (Transaction t = new Transaction(hostDoc, transactionName))
                    {
                        try
                        {
                            t.Start();
                            Logger.Info($"   🔄 Начата транзакция: {transactionName}");

                            RevitLinkOptions linkOptions = new RevitLinkOptions(true);
                            linkOptions.IsRelative = false;

                            LinkLoadResult linkResult = RevitLinkType.Create(hostDoc, modelPath, linkOptions);

                            if (linkResult.ElementId == ElementId.InvalidElementId)
                            {
                                Logger.Warning($"   ⚠️ Не удалось создать тип связи. Результат: {linkResult.LoadResult}");
                                failCount++;
                                t.Commit();
                                continue;
                            }

                            // Получаем созданный тип связи сразу после создания
                            RevitLinkType linkType = hostDoc.GetElement(linkResult.ElementId) as RevitLinkType;
                            Logger.Info($"   ✅ Тип связи создан (ElementId: {linkResult.ElementId}).");

                            RevitLinkInstance linkInstance = RevitLinkInstance.Create(hostDoc, linkResult.ElementId);
                            Logger.Info($"   ✅ Экземпляр связи создан (InstanceId: {linkInstance.Id}).");

                            // 1. ПРОВЕРКА ОБЩИХ КООРДИНАТ
                            Document linkedDoc = linkInstance.GetLinkDocument();
                            bool hasSharedCoords = false;

                            if (linkedDoc != null)
                            {
                                var surveyPoint = new FilteredElementCollector(linkedDoc)
                                    .OfCategory(BuiltInCategory.OST_SharedBasePoint)
                                    .OfClass(typeof(BasePoint))
                                    .FirstOrDefault() as BasePoint;

                                if (surveyPoint != null && surveyPoint.IsShared)
                                {
                                    hasSharedCoords = true;
                                    Logger.Info($"   ✅ Модель использует общие координаты (IsShared = true).");
                                }
                                else
                                {
                                    Logger.Warning($"   ⚠️ Модель НЕ использует общие координаты.");
                                }
                            }

                            if (!hasSharedCoords)
                            {
                                hostDoc.Delete(linkInstance.Id);
                                hostDoc.Delete(linkResult.ElementId);
                                Logger.Info($"   🗑️ Связь и тип удалены (нет общих координат).");
                                skipCount++;
                                t.Commit();
                                continue;
                            }

                            // 2. ОПРЕДЕЛЕНИЕ ЦЕЛЕВОГО РАБОЧЕГО НАБОРА
                            string targetWorksetName = null;
                            foreach (var rule in worksetMapping)
                            {
                                if (fileName.IndexOf(rule.Pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    targetWorksetName = rule.WorksetName;
                                    Logger.Info($"   🔍 Найдено совпадение с правилом: '{rule.Pattern}' -> '{targetWorksetName}'");
                                    break;
                                }
                            }

                            if (!string.IsNullOrEmpty(targetWorksetName))
                            {
                                Workset targetWorkset = GetOrCreateWorkset(hostDoc, targetWorksetName);

                                if (targetWorkset != null)
                                {
                                    // 🔹 ИЗМЕНЕНИЕ РАБОЧЕГО НАБОРА У ТИПА СВЯЗИ (RevitLinkType)
                                    Parameter typeWorksetParam = linkType?.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                                    if (typeWorksetParam != null && !typeWorksetParam.IsReadOnly)
                                    {
                                        typeWorksetParam.Set(targetWorkset.Id.IntegerValue);
                                        Logger.Info($"   📁 Тип связи (RevitLinkType) перемещен в рабочий набор: '{targetWorksetName}'");
                                    }
                                    else
                                    {
                                        Logger.Warning($"   ⚠️ Не удалось изменить рабочий набор типа связи (доступ только для чтения).");
                                    }

                                    // 🔹 ИЗМЕНЕНИЕ РАБОЧЕГО НАБОРА У ЭКЗЕМПЛЯРА СВЯЗИ (RevitLinkInstance)
                                    Parameter instanceWorksetParam = linkInstance.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                                    if (instanceWorksetParam != null && !instanceWorksetParam.IsReadOnly)
                                    {
                                        instanceWorksetParam.Set(targetWorkset.Id.IntegerValue);
                                        Logger.Info($"   📁 Экземпляр связи (RevitLinkInstance) перемещен в рабочий набор: '{targetWorksetName}'");
                                    }
                                    else
                                    {
                                        Logger.Warning($"   ⚠️ Не удалось изменить рабочий набор экземпляра связи (доступ только для чтения).");
                                    }
                                }
                            }
                            else
                            {
                                Logger.Info($"   ℹ️ Совпадений в Excel не найдено. Модель и её тип останутся в рабочих наборах по умолчанию.");
                            }

                            // 3. ЗАКРЕПЛЕНИЕ МОДЕЛИ (PINNING)
                            try
                            {
                                if (!linkInstance.Pinned)
                                {
                                    linkInstance.Pinned = true;
                                    Logger.Info($"   🔒 Экземпляр связи успешно закреплен (Pinned).");
                                }
                                else
                                {
                                    Logger.Info($"   ℹ️ Экземпляр связи уже был закреплен ранее.");
                                }
                            }
                            catch (Exception pinEx)
                            {
                                Logger.Warning($"   ⚠️ Не удалось закрепить связь: {pinEx.Message}");
                            }

                            t.Commit();
                            Logger.Info($"   💾 Транзакция успешно зафиксирована.");
                            successCount++;
                        }
                        catch (Exception txEx)
                        {
                            if (t.HasStarted() && !t.HasEnded())
                            {
                                t.RollBack();
                                Logger.Error($"   ↩️ Транзакция откатана из-за ошибки: {txEx.Message}");
                            }
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"❌ Критическая ошибка при обработке {userVisiblePath}: {ex.Message}");
                    Logger.Error($"   Трассировка: {ex.StackTrace}");
                    failCount++;
                }
            }

            Logger.Info("=== Итоги процесса связывания ===");
            Logger.Info($"✅ Успешно связано и закреплено: {successCount}");
            Logger.Info($"⏭️ Пропущено (нет общих коорд.): {skipCount}");
            Logger.Info($"❌ Ошибок: {failCount}");

            string summaryMessage =
                $"Процесс завершен.\n\n" +
                $"✅ Успешно: {successCount}\n" +
                $"⏭️ Пропущено: {skipCount}\n" +
                $"❌ Ошибок: {failCount}\n\n" +
                $"Примечание: Типы и экземпляры связей распределены по рабочим наборам и закреплены (🔒).";

            MessageBoxIcon icon = (failCount == 0) ? MessageBoxIcon.Information : MessageBoxIcon.Warning;
            MessageBox.Show(summaryMessage, "Результат загрузки моделей", MessageBoxButtons.OK, icon);
        }

        private Workset GetOrCreateWorkset(Document doc, string worksetName)
        {
            var existingWorkset = new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset)
                .FirstOrDefault(w => w.Name.Equals(worksetName, StringComparison.OrdinalIgnoreCase));

            if (existingWorkset != null)
            {
                Logger.Info($"   🔎 Найден существующий рабочий набор: '{worksetName}' (ID: {existingWorkset.Id})");
                return existingWorkset;
            }

            try
            {
                Workset newWorkset = Workset.Create(doc, worksetName);
                Logger.Info($"   ➕ Создан новый рабочий набор: '{worksetName}' (ID: {newWorkset.Id})");
                return newWorkset;
            }
            catch (Exception ex)
            {
                Logger.Error($"   ❌ Не удалось создать рабочий набор '{worksetName}': {ex.Message}");
                return null;
            }
        }
    }
}