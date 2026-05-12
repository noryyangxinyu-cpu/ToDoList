using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows.Threading;

namespace TodoApp
{
    public partial class MainWindow : Window
    {
        private TodoViewModel viewModel;
        private DispatcherTimer autoSaveTimer;
        private DateTime lastSaveTime;
        private Stack<UndoAction> undoStack;
        private string lastImportFilePath;
        private Point? taskDragStartScreen;
        private TaskItem taskDragSourceItem;
        private TaskItem taskContentEditSession;
        private string taskContentEditSnapshot;
        private TaskItem taskDetailsEditSession;
        private string taskDetailsEditSnapshot;

        private static int ComputeTaskDropInsertIndex(ListBox listBox, DragEventArgs e, int itemCount)
        {
            int newIndex = itemCount;
            for (int i = 0; i < itemCount; i++)
            {
                var container = listBox.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
                if (container == null)
                {
                    continue;
                }
                var position = e.GetPosition(container);
                if (position.Y < container.ActualHeight / 2)
                {
                    newIndex = i;
                    break;
                }
                newIndex = i + 1;
            }
            return newIndex;
        }

        private void ClearTaskDropIndicators()
        {
            if (viewModel.CurrentDateTasks == null)
            {
                return;
            }
            foreach (var t in viewModel.CurrentDateTasks)
            {
                t.ShowDropBefore = false;
                t.ShowDropAfter = false;
            }
        }


        public MainWindow()
        {
            InitializeComponent();
            viewModel = new TodoViewModel();
            DataContext = viewModel;
            undoStack = new Stack<UndoAction>();

            // 加载配置
            LoadConfig();

            InitializeSampleTasks();

            // 程序启动时自动导入数据
            AutoImportData();

            // 初始化自动保存状态
            lastSaveTime = DateTime.Now;
            AutoSaveStatusText.Text = $"上次保存: {lastSaveTime.ToString("HH:mm:ss")}";

            // 初始化自动保存定时器
            autoSaveTimer = new DispatcherTimer();
            autoSaveTimer.Interval = TimeSpan.FromSeconds(5); // 5秒后自动保存
            autoSaveTimer.Tick += (s, e) =>
            {
                AutoSave();
                autoSaveTimer.Stop();
            };
        }

        void Window_Closing(object sender, CancelEventArgs e)
        {
            // 窗口关闭时立即保存数据
            ExportData_Click(null, null);
        }

        private string GetDefaultSavePath()
        {
            // 使用Windows文档目录作为默认保存路径
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return System.IO.Path.Combine(documentsPath, "todo_data.json");
        }

        private string GetConfigFilePath()
        {
            // 使用Windows文档目录作为配置文件路径
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return System.IO.Path.Combine(documentsPath, "backlog_config.json");
        }

        private void SaveConfig()
        {
            try
            {
                var configPath = GetConfigFilePath();
                var config = new
                {
                    LastImportFilePath = lastImportFilePath
                };
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
            }
            catch
            {
                // 保存配置失败时不显示错误，避免影响用户体验
            }
        }

        private void LoadConfig()
        {
            try
            {
                var configPath = GetConfigFilePath();
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize<JsonElement>(json);
                    if (config.TryGetProperty("LastImportFilePath", out var pathElement))
                    {
                        lastImportFilePath = pathElement.GetString();
                    }
                }
            }
            catch
            {
                // 加载配置失败时不显示错误，避免影响用户体验
            }
        }

        void AutoImportData()
        {
            try
            {
                // 尝试从上次导入的路径或默认位置导入数据
                string importPath = !string.IsNullOrEmpty(lastImportFilePath) ? lastImportFilePath : GetDefaultSavePath();
                if (File.Exists(importPath))
                {
                    var json = File.ReadAllText(importPath);
                    var importData = JsonSerializer.Deserialize<JsonElement>(json);

                    viewModel.DateGroups.Clear();
                    viewModel.AIPrompts.Clear();

                    if (importData.TryGetProperty("DateGroups", out var dateGroupsElement))
                    {
                        foreach (var dgElement in dateGroupsElement.EnumerateArray())
                        {
                            DateTime date;
                            try
                            {
                                date = dgElement.TryGetProperty("Date", out var dateProp) ? dateProp.GetDateTime() : DateTime.Now;
                            }
                            catch
                            {
                                date = DateTime.Now;
                            }

                            var name = dgElement.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : "未命名分类";
                            var tasks = new ObservableCollection<TaskItem>();

                            if (dgElement.TryGetProperty("Tasks", out var tasksElement))
                            {
                                foreach (var tElement in tasksElement.EnumerateArray())
                                {
                                    var task = new TaskItem
                                    {
                                        Content = tElement.TryGetProperty("Content", out var contentProp) ? contentProp.GetString() : "",
                                        IsCompleted = tElement.TryGetProperty("IsCompleted", out var completedProp) ? completedProp.GetBoolean() : false,
                                        Details = tElement.TryGetProperty("Details", out var detailsProp) ? detailsProp.GetString() : null
                                    };
                                    if (tElement.TryGetProperty("Date", out var taskDateProp))
                                    {
                                        try
                                        {
                                            task.Date = taskDateProp.GetDateTime();
                                        }
                                        catch
                                        {
                                            task.Date = date;
                                        }
                                    }
                                    else
                                    {
                                        task.Date = date;
                                    }
                                    tasks.Add(task);
                                }
                            }

                            var dateGroup = new DateGroup(date)
                            {
                                Name = name,
                                Tasks = tasks
                            };
                            dateGroup.UpdateTaskCount();
                            viewModel.DateGroups.Add(dateGroup);
                        }
                    }

                    if (importData.TryGetProperty("AIPrompts", out var aiPromptsElement))
                    {
                        foreach (var apElement in aiPromptsElement.EnumerateArray())
                        {
                            viewModel.AIPrompts.Add(new AIPrompt
                            {
                                Name = apElement.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : "",
                                Prompt = apElement.TryGetProperty("Prompt", out var promptProp) ? promptProp.GetString() : "",
                                Color = apElement.TryGetProperty("Color", out var colorProp) ? colorProp.GetString() : "#4CAF50"
                            });
                        }
                    }

                    if (viewModel.DateGroups.Count > 0)
                    {
                        viewModel.DateGroups[0].IsSelected = true;
                        viewModel.SelectedCategory = viewModel.DateGroups[0];
                    }

                    StatusTextBox.Text = "自动导入成功，路径：" + importPath;
                }
            }
            catch (Exception)
            {
                // 自动导入失败时不显示错误，避免影响用户体验
                StatusTextBox.Text = "自动导入失败";
            }
        }



        private void InitializeSampleTasks()
        {
            var category1 = new DateGroup(DateTime.Now) { Name = "工作任务" };
            var category2 = new DateGroup(DateTime.Now) { Name = "生活" };
            var category3 = new DateGroup(DateTime.Now) { Name = "灵感" };

            viewModel.DateGroups.Add(category1);
            viewModel.DateGroups.Add(category2);
            viewModel.DateGroups.Add(category3);

            category1.Tasks.Add(new TaskItem { Content = "工作内容", Date = category1.Date });

            category2.Tasks.Add(new TaskItem { Content = "今天要吃啥。", Date = category2.Date, Details = "便当。" });

            category3.Tasks.Add(new TaskItem { Content = "机器人多久能覆盖这个世界？", Date = category3.Date });

            category1.UpdateTaskCount();
            category2.UpdateTaskCount();
            category3.UpdateTaskCount();

            category1.IsSelected = true;
            viewModel.SelectedCategory = category1;
        }

        private void Category_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is DateGroup category)
            {
                if (e.ClickCount == 2)
                {
                    var editWindow = new EditCategoryWindow(category.Name);
                    if (editWindow.ShowDialog() == true && !string.IsNullOrEmpty(editWindow.CategoryName))
                    {
                        category.Name = editWindow.CategoryName;
                    }
                }
                else
                {
                    foreach (var group in viewModel.DateGroups)
                    {
                        group.IsSelected = false;
                    }
                    category.IsSelected = true;
                    viewModel.SelectedCategory = category;
                }
                e.Handled = true;
            }
        }

        private void AddCategory_Click(object sender, RoutedEventArgs e)
        {
            var editWindow = new EditCategoryWindow("");
            if (editWindow.ShowDialog() == true && !string.IsNullOrEmpty(editWindow.CategoryName))
            {
                var newCategory = new DateGroup(DateTime.Now)
                {
                    Name = editWindow.CategoryName
                };
                viewModel.DateGroups.Add(newCategory);
                
                // 添加一个默认任务
                newCategory.Tasks.Add(new TaskItem { Content = "", Date = newCategory.Date });
                newCategory.UpdateTaskCount();
                
                newCategory.IsSelected = true;
                viewModel.SelectedCategory = newCategory;
            }
        }

        private void RenameCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is DateGroup category)
            {
                var editWindow = new EditCategoryWindow(category.Name);
                if (editWindow.ShowDialog() == true && !string.IsNullOrEmpty(editWindow.CategoryName))
                {
                    category.Name = editWindow.CategoryName;
                }
            }
        }

        private void DeleteCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is DateGroup category)
            {
                if (MessageBox.Show($"确定要删除分类 '{category.Name}' 吗？", "删除确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    int index = viewModel.DateGroups.IndexOf(category);
                    viewModel.DateGroups.Remove(category);
                    
                    // 选择下一个分类
                    if (viewModel.DateGroups.Count > 0)
                    {
                        int newIndex = Math.Min(index, viewModel.DateGroups.Count - 1);
                        viewModel.DateGroups[newIndex].IsSelected = true;
                        viewModel.SelectedCategory = viewModel.DateGroups[newIndex];
                    }
                    else
                    {
                        viewModel.SelectedCategory = null;
                    }
                }
            }
        }

        void DeleteTask_Click(object sender, RoutedEventArgs e)
        {
            TaskItem taskItem = null;
            if (sender is Button button)
            {
                taskItem = button.Tag as TaskItem;
            }
            else if (sender is MenuItem menuItem)
            {
                taskItem = menuItem.Tag as TaskItem;
            }
            if (taskItem != null)
            {
                MessageBoxResult result = MessageBox.Show(
                    "确定要删除这个任务吗？",
                    "确认删除",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    // 保存删除的任务信息到历史记录栈（仅当不是最后一个任务时）
                    var group = viewModel.DateGroups.FirstOrDefault(dg => dg.Tasks.Contains(taskItem));
                    if (group != null && group.Tasks.Count > 1)
                    {
                        int index = group.Tasks.IndexOf(taskItem);
                        if (index >= 0)
                        {
                            undoStack.Push(new UndoTaskDeleteAction(this, taskItem, group, index));
                        }
                    }
                    viewModel.DeleteTask(taskItem);
                    TriggerAutoSave();
                }
            }
        }

        private void TaskContent_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox && ((TextBox)sender).DataContext is TaskItem t)
            {
                taskContentEditSession = t;
                taskContentEditSnapshot = t.Content ?? "";
            }
        }

        private void TaskContent_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox && ((TextBox)sender).DataContext is TaskItem t && ReferenceEquals(taskContentEditSession, t))
            {
                var current = t.Content ?? "";
                if (current != taskContentEditSnapshot)
                {
                    undoStack.Push(new UndoTaskFieldEditAction(this, t, true, taskContentEditSnapshot));
                }
                taskContentEditSession = null;
            }
        }

        private void TaskDetails_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox && ((TextBox)sender).DataContext is TaskItem t)
            {
                taskDetailsEditSession = t;
                taskDetailsEditSnapshot = t.Details;
            }
        }

        private void TaskDetails_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox && ((TextBox)sender).DataContext is TaskItem t && ReferenceEquals(taskDetailsEditSession, t))
            {
                if (!string.Equals(t.Details, taskDetailsEditSnapshot))
                {
                    undoStack.Push(new UndoTaskFieldEditAction(this, t, false, taskDetailsEditSnapshot));
                }
                taskDetailsEditSession = null;
            }
        }

        private void PerformUndo()
        {
            if (undoStack.Count == 0)
            {
                return;
            }
            undoStack.Pop().ApplyUndo();
        }

        private void TaskItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 不再切换详情栏显示，由详情按钮控制
        }

        private void TaskDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TaskItem t)
            {
                taskDragStartScreen = PointToScreen(e.GetPosition(this));
                taskDragSourceItem = t;
            }
        }

        private void TaskDragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (taskDragStartScreen is Point startScreen && taskDragSourceItem != null && e.LeftButton == MouseButtonState.Pressed && sender is FrameworkElement fe)
            {
                var curScreen = PointToScreen(e.GetPosition(this));
                var dx = curScreen.X - startScreen.X;
                var dy = curScreen.Y - startScreen.Y;
                if (dx * dx + dy * dy > SystemParameters.MinimumHorizontalDragDistance * SystemParameters.MinimumHorizontalDragDistance)
                {
                    var dragTask = taskDragSourceItem;
                    dragTask.IsDragSource = true;
                    void GiveFeedbackHandler(object _, GiveFeedbackEventArgs args)
                    {
                        args.UseDefaultCursors = false;
                        Mouse.SetCursor(Cursors.SizeAll);
                    }
                    fe.GiveFeedback += GiveFeedbackHandler;
                    try
                    {
                        DragDrop.DoDragDrop(fe, dragTask, DragDropEffects.Move);
                    }
                    finally
                    {
                        fe.GiveFeedback -= GiveFeedbackHandler;
                        dragTask.IsDragSource = false;
                        ClearTaskDropIndicators();
                        Mouse.SetCursor(null);
                        taskDragStartScreen = null;
                        taskDragSourceItem = null;
                    }
                }
            }
        }

        private void TaskDragHandle_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            taskDragStartScreen = null;
            taskDragSourceItem = null;
        }

        private void ToggleDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is TaskItem taskItem)
            {
                taskItem.ShowDetails = !taskItem.ShowDetails;
            }
        }

        void CopyTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is TaskItem taskItem)
            {
                string content = $"【{taskItem.Content}】";
                string details = taskItem.Details;
                string copyText = content;
                if (!string.IsNullOrEmpty(details))
                {
                    copyText += Environment.NewLine + details;
                }
                Clipboard.SetText(copyText);
            }
        }

        private void MoveTaskToTop_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is TaskItem taskItem)
            {
                DateGroup group = viewModel.DateGroups.FirstOrDefault(dg => dg.Tasks.Contains(taskItem));
                if (group != null)
                {
                    int index = group.Tasks.IndexOf(taskItem);
                    if (index > 0)
                    {
                        group.Tasks.RemoveAt(index);
                        group.Tasks.Insert(0, taskItem);
                        if (viewModel.SelectedCategory == group)
                        {
                            viewModel.UpdateCurrentDateTasks();
                        }
                        TriggerAutoSave();
                    }
                }
            }
        }

        private void MoveTaskToBottom_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is TaskItem taskItem)
            {
                DateGroup group = viewModel.DateGroups.FirstOrDefault(dg => dg.Tasks.Contains(taskItem));
                if (group != null)
                {
                    int index = group.Tasks.IndexOf(taskItem);
                    if (index < group.Tasks.Count - 1)
                    {
                        group.Tasks.RemoveAt(index);
                        group.Tasks.Add(taskItem);
                        if (viewModel.SelectedCategory == group)
                        {
                            viewModel.UpdateCurrentDateTasks();
                        }
                        TriggerAutoSave();
                    }
                }
            }
        }

        private void CheckBox_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is CheckBox checkBox && checkBox.DataContext is TaskItem taskItem && viewModel.SelectedDate.HasValue)
            {
                var group = viewModel.DateGroups.FirstOrDefault(dg => dg.Date.Date == viewModel.SelectedDate.Value.Date);
                if (group != null)
                {
                    group.UpdateTaskCount();
                }
            }
        }

        void AddTaskAfter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is TaskItem taskItem)
            {
                var group = viewModel.DateGroups.FirstOrDefault(dg => dg.Tasks.Contains(taskItem));
                if (group != null)
                {
                    int index = group.Tasks.IndexOf(taskItem);
                    if (index >= 0)
                    {
                        group.Tasks.Insert(index + 1, new TaskItem { Content = "", Date = group.Date });
                        group.UpdateTaskCount();
                        if (viewModel.SelectedCategory == group)
                        {
                            viewModel.UpdateCurrentDateTasks();
                        }
                        TriggerAutoSave();
                    }
                }
            }
        }

        private void TaskListBox_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(TaskItem)))
            {
                e.Effects = DragDropEffects.Move;
            }
        }

        private void TaskListBox_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(TaskItem)))
            {
                return;
            }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            var listBox = sender as ListBox;
            if (listBox == null || !string.IsNullOrEmpty(viewModel.SearchText) || viewModel.SelectedCategory == null)
            {
                ClearTaskDropIndicators();
                return;
            }
            var items = viewModel.CurrentDateTasks;
            if (items == null || items.Count == 0)
            {
                ClearTaskDropIndicators();
                return;
            }
            int insertIndex = ComputeTaskDropInsertIndex(listBox, e, items.Count);
            foreach (var t in items)
            {
                t.ShowDropBefore = false;
                t.ShowDropAfter = false;
            }
            if (insertIndex < items.Count)
            {
                items[insertIndex].ShowDropBefore = true;
            }
            else
            {
                items[items.Count - 1].ShowDropAfter = true;
            }
        }

        private void TaskListBox_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(TaskItem)))
            {
                e.Handled = true;
                return;
            }
            var droppedTask = e.Data.GetData(typeof(TaskItem)) as TaskItem;
            if (droppedTask == null || !string.IsNullOrEmpty(viewModel.SearchText) || viewModel.SelectedCategory == null)
            {
                e.Handled = true;
                return;
            }
            var group = viewModel.SelectedCategory;
            if (!group.Tasks.Contains(droppedTask))
            {
                e.Handled = true;
                return;
            }
            var listBox = sender as ListBox;
            var items = listBox?.ItemsSource as ObservableCollection<TaskItem>;
            if (items == null || items != viewModel.CurrentDateTasks)
            {
                e.Handled = true;
                return;
            }
            int oldIndex = group.Tasks.IndexOf(droppedTask);
            if (oldIndex < 0)
            {
                e.Handled = true;
                return;
            }
            ClearTaskDropIndicators();
            int newIndex = ComputeTaskDropInsertIndex(listBox, e, items.Count);
            if (newIndex == oldIndex)
            {
                e.Handled = true;
                return;
            }
            if (newIndex > oldIndex)
            {
                newIndex--;
            }
            group.Tasks.RemoveAt(oldIndex);
            group.Tasks.Insert(newIndex, droppedTask);
            viewModel.UpdateCurrentDateTasks();
            TriggerAutoSave();
            e.Handled = true;
        }

        private void TaskListBox_DragLeave(object sender, DragEventArgs e)
        {
        }

        private void TaskListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var eventArg = new MouseWheelEventArgs(
                e.MouseDevice, e.Timestamp, e.Delta);
            eventArg.RoutedEvent = UIElement.MouseWheelEvent;
            eventArg.Source = sender;
            var parent = ((Control)sender).Parent as UIElement;
            parent.RaiseEvent(eventArg);
        }

        void AddAIPrompt_Click(object sender, RoutedEventArgs e)
        {
            var editWindow = new EditAIPromptWindow();
            if (editWindow.ShowDialog() == true && !string.IsNullOrEmpty(editWindow.PromptName) && !string.IsNullOrEmpty(editWindow.PromptText))
            {
                var colors = new[] { "#9C27B0", "#4CAF50", "#2196F3", "#FF9800", "#F44336", "#607D8B", "#795548", "#9E9E9E" };
                var random = new Random();
                var color = colors[random.Next(colors.Length)];
                
                viewModel.AIPrompts.Add(new AIPrompt
                {
                    Name = editWindow.PromptName,
                    Prompt = editWindow.PromptText,
                    Color = color
                });
                
                // 复制到剪贴板
                Clipboard.SetText(editWindow.PromptText);
            }
        }

        void AIPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is AIPrompt prompt)
            {
                viewModel.ThoughtContent = prompt.Prompt;
                // 复制到剪贴板
                Clipboard.SetText(prompt.Prompt);
                StatusTextBox.Text = "复制到剪切板";
            }
        }

        void EditAIPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is AIPrompt prompt)
            {
                var editWindow = new EditAIPromptWindow(prompt);
                if (editWindow.ShowDialog() == true && !string.IsNullOrEmpty(editWindow.PromptName))
                {
                    prompt.Name = editWindow.PromptName;
                    prompt.Prompt = editWindow.PromptText;
                }
            }
        }

        void DeleteAIPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is AIPrompt prompt)
            {
                var result = MessageBox.Show(
                    $"确定要删除快捷输入 \"{prompt.Name}\" 吗？",
                    "确认删除",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    viewModel.AIPrompts.Remove(prompt);
                }
            }
        }

        void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                ExportData_Click(null, null);
            }
            else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (Keyboard.FocusedElement is TextBox focusTb && focusTb.DataContext is TaskItem)
                {
                    return;
                }
                e.Handled = true;
                PerformUndo();
            }
        }



        void ExportData_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 使用导入文件路径或默认保存路径
                string savePath = !string.IsNullOrEmpty(lastImportFilePath) ? lastImportFilePath : GetDefaultSavePath();

                var exportData = new
                {
                    DateGroups = viewModel.DateGroups.Select(dg => new
                    {
                        Date = dg.Date,
                        Name = dg.Name,
                        Tasks = dg.Tasks.Select(t => new
                        {
                            Content = t.Content,
                            IsCompleted = t.IsCompleted,
                            Details = t.Details
                        }).ToList()
                    }).ToList(),
                    AIPrompts = viewModel.AIPrompts.Select(ap => new
                    {
                        Name = ap.Name,
                        Prompt = ap.Prompt,
                        Color = ap.Color
                    }).ToList()
                };

                var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(savePath, json);
                
                // 在状态信息栏显示保存位置
                StatusTextBox.Text = "保存成功，路径：" + savePath;
                
                // 更新上次保存时间
                lastSaveTime = DateTime.Now;
                AutoSaveStatusText.Text = $"上次保存: {lastSaveTime.ToString("HH:mm:ss")}";
            }
            catch (Exception ex)
            {
                StatusTextBox.Text = "保存失败: " + ex.Message;
            }
        }

        void TriggerAutoSave()
        {
            autoSaveTimer?.Stop();
            autoSaveTimer?.Start();
            AutoSaveStatusText.Text = "正在自动保存...";
        }

        void AutoSave()
        {
            try
            {
                // 使用导入文件路径或默认保存路径
                var savePath = !string.IsNullOrEmpty(lastImportFilePath) ? lastImportFilePath : GetDefaultSavePath();
                var exportData = new
                {
                    DateGroups = viewModel.DateGroups.Select(dg => new
                    {
                        Date = dg.Date,
                        Name = dg.Name,
                        Tasks = dg.Tasks.Select(t => new
                        {
                            Content = t.Content,
                            IsCompleted = t.IsCompleted,
                            Details = t.Details
                        }).ToList()
                    }).ToList(),
                    AIPrompts = viewModel.AIPrompts.Select(ap => new
                    {
                        Name = ap.Name,
                        Prompt = ap.Prompt,
                        Color = ap.Color
                    }).ToList()
                };

                var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(savePath, json);
                lastSaveTime = DateTime.Now;
                AutoSaveStatusText.Text = $"上次保存: {lastSaveTime.ToString("HH:mm:ss")}";
            }
            catch (Exception)
            {
                // 自动保存失败时不显示错误，避免影响用户体验
                AutoSaveStatusText.Text = "自动保存失败";
            }
        }

        void ImportData_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "JSON文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    // 保存导入文件路径
                    lastImportFilePath = openFileDialog.FileName;
                    // 保存配置
                    SaveConfig();
                    
                    var json = File.ReadAllText(openFileDialog.FileName);
                    var importData = JsonSerializer.Deserialize<JsonElement>(json);

                    viewModel.DateGroups.Clear();
                    viewModel.AIPrompts.Clear();

                    if (importData.TryGetProperty("DateGroups", out var dateGroupsElement))
                    {
                        foreach (var dgElement in dateGroupsElement.EnumerateArray())
                        {
                            DateTime date;
                            try
                            {
                                date = dgElement.TryGetProperty("Date", out var dateProp) ? dateProp.GetDateTime() : DateTime.Now;
                            }
                            catch
                            {
                                date = DateTime.Now;
                            }

                            var name = dgElement.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : "未命名分类";
                            var tasks = new ObservableCollection<TaskItem>();

                            if (dgElement.TryGetProperty("Tasks", out var tasksElement))
                            {
                                foreach (var tElement in tasksElement.EnumerateArray())
                                {
                                    var task = new TaskItem
                                    {
                                        Content = tElement.TryGetProperty("Content", out var contentProp) ? contentProp.GetString() : "",
                                        IsCompleted = tElement.TryGetProperty("IsCompleted", out var completedProp) ? completedProp.GetBoolean() : false,
                                        Details = tElement.TryGetProperty("Details", out var detailsProp) ? detailsProp.GetString() : null
                                    };
                                    if (tElement.TryGetProperty("Date", out var taskDateProp))
                                    {
                                        try
                                        {
                                            task.Date = taskDateProp.GetDateTime();
                                        }
                                        catch
                                        {
                                            task.Date = date;
                                        }
                                    }
                                    else
                                    {
                                        task.Date = date;
                                    }
                                    tasks.Add(task);
                                }
                            }

                            var dateGroup = new DateGroup(date)
                            {
                                Name = name,
                                Tasks = tasks
                            };
                            dateGroup.UpdateTaskCount();
                            viewModel.DateGroups.Add(dateGroup);
                        }
                    }

                    if (importData.TryGetProperty("AIPrompts", out var aiPromptsElement))
                    {
                        foreach (var apElement in aiPromptsElement.EnumerateArray())
                        {
                            viewModel.AIPrompts.Add(new AIPrompt
                            {
                                Name = apElement.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : "",
                                Prompt = apElement.TryGetProperty("Prompt", out var promptProp) ? promptProp.GetString() : "",
                                Color = apElement.TryGetProperty("Color", out var colorProp) ? colorProp.GetString() : "#4CAF50"
                            });
                        }
                    }

                    if (viewModel.DateGroups.Count > 0)
                    {
                        viewModel.DateGroups[0].IsSelected = true;
                        viewModel.SelectedCategory = viewModel.DateGroups[0];
                        viewModel.UpdateCurrentDateTasks();
                    }

                    StatusTextBox.Text = "导入成功，路径：" + lastImportFilePath;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入失败：{ex.Message}");
            }
        }

        void CopyAll_Click(object sender, RoutedEventArgs e)
        {
            var content = "";
            foreach (var group in viewModel.DateGroups)
            {
                content += $"{group.Name}\n";
                foreach (var task in group.Tasks)
                {
                    if (task.IsCompleted)
                    {
                        content += $"✓ {task.Content}\n";
                    }
                    else
                    {
                        content += $"- {task.Content}\n";
                    }
                    if (!string.IsNullOrEmpty(task.Details))
                    {
                        content += $"  {task.Details}\n";
                    }
                    content += "\n";
                }
                content += "\n";
            }
            Clipboard.SetText(content);
        }

        private void CopyPageStyle1_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is DateGroup category)
            {
                var content = $"---【{category.Name}】\n";
                foreach (var task in category.Tasks)
                {
                    content += $"【{task.Content}】\n";
                    if (!string.IsNullOrEmpty(task.Details))
                    {
                        content += $"{task.Details}\n";
                    }
                    content += "\n"; // 结束时换行
                }
                Clipboard.SetText(content);
            }
        }

        private void CopyPageStyle2_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is DateGroup category)
            {
                var content = $"【{category.Name}】\n";
                foreach (var task in category.Tasks)
                {
                    if (task.IsCompleted)
                    {
                        content += $"✓ {task.Content}\n";
                    }
                    else
                    {
                        content += $"- {task.Content}\n";
                    }
                }
                Clipboard.SetText(content);
            }
        }

        private void CopyAllStyle1_Click(object sender, RoutedEventArgs e)
        {
            var content = "";
            foreach (var group in viewModel.DateGroups)
            {
                content += $"---【{group.Name}】\n";
                foreach (var task in group.Tasks)
                {
                    content += $"【{task.Content}】\n";
                    if (!string.IsNullOrEmpty(task.Details))
                    {
                        content += $"{task.Details}\n";
                    }
                    content += "\n"; // 结束时换行
                }
                content += "\n"; // 结束时换行
            }
            Clipboard.SetText(content);
        }

        private void CopyAllStyle2_Click(object sender, RoutedEventArgs e)
        {
            var content = "";
            foreach (var group in viewModel.DateGroups)
            {
                content += $"【{group.Name}】\n";
                foreach (var task in group.Tasks)
                {
                    if (task.IsCompleted)
                    {
                        content += $"✓ {task.Content}\n";
                    }
                    else
                    {
                        content += $"- {task.Content}\n";
                    }
                }
            }
            Clipboard.SetText(content);
        }

        private void ClearCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is DateGroup category)
            {
                // 显示确认对话框
                MessageBoxResult result = MessageBox.Show(
                    "确定要清除当前概览的所有任务吗？清除后将只保留一个空的任务栏。",
                    "确认清除",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    // 清除所有任务
                    category.Tasks.Clear();
                    // 添加一个空的任务栏
                    category.Tasks.Add(new TaskItem { Content = "", ShowDetails = false, Date = category.Date });
                    // 更新任务计数
                    category.UpdateTaskCount();
                    // 刷新当前显示的任务列表
                    viewModel.UpdateCurrentDateTasks();
                    // 触发自动保存
                    TriggerAutoSave();
                }
            }
        }

        private void MoveCategoryUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is DateGroup category)
            {
                int index = viewModel.DateGroups.IndexOf(category);
                if (index > 0)
                {
                    viewModel.DateGroups.RemoveAt(index);
                    viewModel.DateGroups.Insert(index - 1, category);
                    TriggerAutoSave();
                }
            }
        }

        private void MoveCategoryDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is DateGroup category)
            {
                int index = viewModel.DateGroups.IndexOf(category);
                if (index < viewModel.DateGroups.Count - 1)
                {
                    viewModel.DateGroups.RemoveAt(index);
                    viewModel.DateGroups.Insert(index + 1, category);
                    TriggerAutoSave();
                }
            }
        }

        private void MoveCategoryToTop_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is DateGroup category)
            {
                int index = viewModel.DateGroups.IndexOf(category);
                if (index > 0)
                {
                    viewModel.DateGroups.RemoveAt(index);
                    viewModel.DateGroups.Insert(0, category);
                    TriggerAutoSave();
                }
            }
        }

        private void MoveCategoryToBottom_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is DateGroup category)
            {
                int index = viewModel.DateGroups.IndexOf(category);
                if (index < viewModel.DateGroups.Count - 1)
                {
                    viewModel.DateGroups.RemoveAt(index);
                    viewModel.DateGroups.Add(category);
                    TriggerAutoSave();
                }
            }
        }

        private abstract class UndoAction
        {
            protected readonly MainWindow Owner;

            protected UndoAction(MainWindow owner)
            {
                Owner = owner;
            }

            internal abstract void ApplyUndo();
        }

        private sealed class UndoTaskDeleteAction : UndoAction
        {
            private readonly TaskItem task;
            private readonly DateGroup group;
            private readonly int index;

            public UndoTaskDeleteAction(MainWindow owner, TaskItem task, DateGroup group, int index) : base(owner)
            {
                this.task = task;
                this.group = group;
                this.index = index;
            }

            internal override void ApplyUndo()
            {
                if (group == null || task == null)
                {
                    return;
                }
                if (index >= 0 && index <= group.Tasks.Count)
                {
                    group.Tasks.Insert(index, task);
                }
                else
                {
                    group.Tasks.Add(task);
                }
                group.UpdateTaskCount();
                if (Owner.viewModel.SelectedCategory == group)
                {
                    Owner.viewModel.UpdateCurrentDateTasks();
                }
                Owner.TriggerAutoSave();
            }
        }

        private sealed class UndoTaskFieldEditAction : UndoAction
        {
            private readonly TaskItem task;
            private readonly bool contentField;
            private readonly string previousValue;

            public UndoTaskFieldEditAction(MainWindow owner, TaskItem task, bool contentField, string previousValue) : base(owner)
            {
                this.task = task;
                this.contentField = contentField;
                this.previousValue = previousValue;
            }

            internal override void ApplyUndo()
            {
                if (task == null)
                {
                    return;
                }
                if (contentField)
                {
                    task.Content = previousValue ?? "";
                }
                else
                {
                    task.Details = previousValue;
                }
                Owner.viewModel.UpdateCurrentDateTasks();
                Owner.TriggerAutoSave();
            }
        }

    }

    public class EditCategoryWindow : Window
    {
        private TextBox nameTextBox;
        public string CategoryName { get; private set; }

        public EditCategoryWindow(string initialName)
        {
            Title = "编辑分类名称";
            Width = 350;
            Height = 200;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.White;
            
            // 添加阴影效果
            Effect = new DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 3,
                Color = Color.FromRgb(0, 0, 0),
                Opacity = 0.3
            };

            var stackPanel = new StackPanel { Margin = new Thickness(20) };

            var label = new TextBlock { Text = "分类名称:", FontSize = 14, Margin = new Thickness(0, 0, 0, 10) };
            stackPanel.Children.Add(label);

            nameTextBox = new TextBox { Text = initialName, FontSize = 14, Padding = new Thickness(8) };
            stackPanel.Children.Add(nameTextBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var okButton = new Button { Content = "确定", Width = 80, Height = 30, Margin = new Thickness(0, 0, 10, 0) };
            var cancelButton = new Button { Content = "取消", Width = 80, Height = 30 };

            okButton.Click += (s, e) =>
            {
                CategoryName = nameTextBox.Text.Trim();
                DialogResult = true;
            };
            cancelButton.Click += (s, e) => DialogResult = false;

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            stackPanel.Children.Add(buttonPanel);

            Content = stackPanel;

            Loaded += (s, e) => nameTextBox.Focus();
        }


    }

    public class EditAIPromptWindow : Window
    {
        private TextBox nameTextBox;
        private TextBox promptTextBox;
        public string PromptName { get; private set; }
        public string PromptText { get; private set; }

        public EditAIPromptWindow()
        {
            Title = "新增快捷输入";
            Width = 400;
            Height = 350;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.White;
            
            InitializeControls();
        }

        public EditAIPromptWindow(AIPrompt prompt)
        {
            Title = "编辑快捷输入";
            Width = 400;
            Height = 350;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.White;
            
            InitializeControls();
            
            // 填充现有数据
            nameTextBox.Text = prompt.Name;
            promptTextBox.Text = prompt.Prompt;
        }

        private void InitializeControls()
        {
            // 添加阴影效果
            Effect = new DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 3,
                Color = Color.FromRgb(0, 0, 0),
                Opacity = 0.3
            };

            var stackPanel = new StackPanel { Margin = new Thickness(20) };

            var nameLabel = new TextBlock { Text = "快捷输入名称:", FontSize = 14, Margin = new Thickness(0, 0, 0, 10) };
            stackPanel.Children.Add(nameLabel);

            nameTextBox = new TextBox { FontSize = 14, Padding = new Thickness(8), MaxLength = 15 };
            nameTextBox.TextChanged += NameTextBox_TextChanged;
            stackPanel.Children.Add(nameTextBox);

            var promptLabel = new TextBlock { Text = "快捷输入内容:", FontSize = 14, Margin = new Thickness(0, 15, 0, 10) };
            stackPanel.Children.Add(promptLabel);

            promptTextBox = new TextBox { FontSize = 14, Padding = new Thickness(8), Height = 100, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
            stackPanel.Children.Add(promptTextBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
            var okButton = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelButton = new Button { Content = "取消", Width = 70 };

            okButton.Click += (s, e) =>
            {
                PromptName = nameTextBox.Text.Trim();
                PromptText = promptTextBox.Text.Trim();
                DialogResult = true;
            };
            cancelButton.Click += (s, e) => DialogResult = false;

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            stackPanel.Children.Add(buttonPanel);

            Content = stackPanel;

            Loaded += (s, e) => nameTextBox.Focus();
        }

        private void NameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null)
            {
                var text = textBox.Text;
                int chineseCharCount = 0;

                foreach (char c in text)
                {
                    if (c >= 0x4e00 && c <= 0x9fff) // Unicode中文范围
                    {
                        chineseCharCount++;
                    }
                }

                // 限制中文最大5个字符
                if (chineseCharCount > 5)
                {
                    int maxLength = 5;
                    int currentCount = 0;
                    int index = 0;
                    while (currentCount < maxLength && index < text.Length)
                    {
                        if (text[index] >= 0x4e00 && text[index] <= 0x9fff)
                        {
                            currentCount++;
                        }
                        index++;
                    }
                    textBox.Text = text.Substring(0, index);
                    textBox.SelectionStart = textBox.Text.Length;
                }
            }
        }
    }

    public class TodoViewModel : INotifyPropertyChanged
    {
        private ObservableCollection<DateGroup> dateGroups;
        private DateGroup selectedCategory;
        private ObservableCollection<TaskItem> currentDateTasks;
        private ObservableCollection<AIPrompt> aiPrompts;
        private string searchText;
        private string thoughtContent;

        public ObservableCollection<DateGroup> DateGroups
        {
            get { return dateGroups; }
            set 
            {
                if (dateGroups != null)
                {
                    dateGroups.CollectionChanged -= DateGroups_CollectionChanged;
                }
                
                dateGroups = value; 
                OnPropertyChanged();
                
                if (dateGroups != null)
                {
                    dateGroups.CollectionChanged += DateGroups_CollectionChanged;
                    UpdateDateGroupPositions();
                }
            }
        }

        public DateGroup SelectedCategory
        {
            get { return selectedCategory; }
            set
            {
                // 将所有概览的IsSelected设置为false
                if (DateGroups != null)
                {
                    foreach (var group in DateGroups)
                    {
                        group.IsSelected = false;
                    }
                }
                
                // 设置新的选中概览
                selectedCategory = value;
                if (selectedCategory != null)
                {
                    selectedCategory.IsSelected = true;
                }
                
                UpdateCurrentDateTasks();
                OnPropertyChanged();
            }
        }

        public DateTime? SelectedDate
        {
            get { return selectedCategory?.Date; }
            set
            {
                if (value.HasValue)
                {
                    selectedCategory = DateGroups.FirstOrDefault(dg => dg.Date.Date == value.Value.Date);
                }
                else
                {
                    selectedCategory = null;
                }
                UpdateCurrentDateTasks();
                OnPropertyChanged();
            }
        }

        public ObservableCollection<TaskItem> CurrentDateTasks
        {
            get { return currentDateTasks; }
            set { currentDateTasks = value; OnPropertyChanged(); }
        }

        public string SearchText
        {
            get { return searchText; }
            set
            {
                searchText = value;
                UpdateCurrentDateTasks();
                OnPropertyChanged();
            }
        }

        public string ThoughtContent
        {
            get { return thoughtContent; }
            set { thoughtContent = value; OnPropertyChanged(); }
        }

        public ObservableCollection<AIPrompt> AIPrompts
        {
            get { return aiPrompts; }
            set { aiPrompts = value; OnPropertyChanged(); }
        }

        public TodoViewModel()
        {
            DateGroups = new ObservableCollection<DateGroup>();
            CurrentDateTasks = new ObservableCollection<TaskItem>();
            AIPrompts = new ObservableCollection<AIPrompt>
            {
                new AIPrompt { Name = "Web项目", Prompt = "用3个文件index.html、script.js、stlye.css实现一个Web项目。", Color = "#4CAF50" }
            };
        }

        private void DateGroups_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateDateGroupPositions();
        }

        private void UpdateDateGroupPositions()
        {
            if (DateGroups != null)
            {
                for (int i = 0; i < DateGroups.Count; i++)
                {
                    DateGroups[i].IsFirst = (i == 0);
                    DateGroups[i].IsLast = (i == DateGroups.Count - 1);
                }
            }
        }

        public void AddTask(DateGroup group, string content)
        {
            if (group != null)
            {
                group.Tasks.Add(new TaskItem { Content = content, Date = group.Date });
                group.UpdateTaskCount();
                if (selectedCategory == group)
                {
                    UpdateCurrentDateTasks();
                }
            }
        }

        public void DeleteTask(TaskItem task)
        {
            foreach (var group in DateGroups)
            {
                if (group.Tasks.Contains(task))
                {
                    if (group.Tasks.Count > 1)
                    {
                        group.Tasks.Remove(task);
                    }
                    else
                    {
                        // 如果是最后一个任务，清空内容而非删除
                        task.Content = "";
                        task.Details = "";
                        task.IsCompleted = false;
                        task.ShowDetails = false;
                    }
                    group.UpdateTaskCount();
                    if (selectedCategory == group)
                    {
                        UpdateCurrentDateTasks();
                    }
                    break;
                }
            }
        }

        public void UpdateCurrentDateTasks()
        {
            // 清空当前集合
            CurrentDateTasks.Clear();
            
            if (!string.IsNullOrEmpty(SearchText))
            {
                // 搜索所有分类的条目
                var filteredTasks = DateGroups.SelectMany(dg => dg.Tasks)
                    .Where(t =>
                        t.Content.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                        (t.Details != null && t.Details.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                    )
                    .OrderBy(t => t.Date)
                    .ToList();
                
                // 将筛选后的任务添加到现有集合中
                foreach (var task in filteredTasks)
                {
                    CurrentDateTasks.Add(task);
                }
            }
            else if (selectedCategory != null)
            {
                // 如果没有搜索文本，显示当前分类的所有任务
                foreach (var task in selectedCategory.Tasks)
                {
                    CurrentDateTasks.Add(task);
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class DateGroup : INotifyPropertyChanged
    {
        private DateTime date;
        private ObservableCollection<TaskItem> tasks;
        private string displayText;
        private string taskCountText;
        private string name;
        private bool isSelected;
        private double progress;
        private bool isFirst;
        private bool isLast;

        public DateTime Date
        {
            get { return date; }
            set { date = value; OnPropertyChanged(); }
        }

        public ObservableCollection<TaskItem> Tasks
        {
            get { return tasks; }
            set 
            { 
                if (tasks != null)
                {
                    tasks.CollectionChanged -= Tasks_CollectionChanged;
                    foreach (var task in tasks)
                    {
                        task.PropertyChanged -= Task_PropertyChanged;
                    }
                }
                
                tasks = value; 
                OnPropertyChanged();
                
                if (tasks != null)
                {
                    tasks.CollectionChanged += Tasks_CollectionChanged;
                    foreach (var task in tasks)
                    {
                        task.PropertyChanged += Task_PropertyChanged;
                    }
                }
            }
        }

        public string DisplayText
        {
            get { return displayText; }
            set { displayText = value; OnPropertyChanged(); }
        }

        public string TaskCountText
        {
            get { return taskCountText; }
            set { taskCountText = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get { return name; }
            set { name = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get { return isSelected; }
            set { isSelected = value; OnPropertyChanged(); }
        }

        public double Progress
        {
            get { return progress; }
            set { progress = value; OnPropertyChanged(); }
        }

        public bool IsFirst
        {
            get { return isFirst; }
            set { isFirst = value; OnPropertyChanged(); }
        }

        public bool IsLast
        {
            get { return isLast; }
            set { isLast = value; OnPropertyChanged(); }
        }



        public DateGroup(DateTime date)
        {
            this.date = date;
            Tasks = new ObservableCollection<TaskItem>();
            UpdateDisplayText();
            UpdateTaskCount();
        }

        public void UpdateDisplayText()
        {
            if (date.Date == DateTime.Today)
            {
                DisplayText = $"今天 {date.Month}/{date.Day}";
            }
            else if (date.Date == DateTime.Today.AddDays(-1))
            {
                DisplayText = $"昨天 {date.Month}/{date.Day}";
            }
            else if (date.Date == DateTime.Today.AddDays(1))
            {
                DisplayText = $"明天 {date.Month}/{date.Day}";
            }
            else
            {
                DisplayText = $"{date.Month}月{date.Day}日";
            }
        }

        public void UpdateTaskCount()
        {
            int count = Tasks.Count;
            int completed = Tasks.Count(t => t.IsCompleted);
            if (count == 0)
            {
                TaskCountText = "0 项";
                Progress = 0;
            }
            else if (completed == count)
            {
                TaskCountText = "✓ 全部完成";
                Progress = 100;
            }
            else
            {
                TaskCountText = $"{count - completed} 项待办";
                Progress = (double)completed / count * 100;
            }
        }

        private void Tasks_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (TaskItem task in e.NewItems)
                {
                    task.PropertyChanged += Task_PropertyChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (TaskItem task in e.OldItems)
                {
                    task.PropertyChanged -= Task_PropertyChanged;
                }
            }

            UpdateTaskPositions();
        }

        private void UpdateTaskPositions()
        {
            for (int i = 0; i < Tasks.Count; i++)
            {
                Tasks[i].IsFirst = (i == 0);
                Tasks[i].IsLast = (i == Tasks.Count - 1);
            }
        }

        private void Task_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TaskItem.IsCompleted))
            {
                UpdateTaskCount();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }



    public class TaskItem : INotifyPropertyChanged
    {
        private string content;
        private bool isCompleted;
        private DateTime date;
        private string details;
        private bool showDetails;
        private bool isFirst;
        private bool isLast;
        private bool isDragSource;
        private bool showDropBefore;
        private bool showDropAfter;

        public string Content
        {
            get { return content; }
            set { content = value; OnPropertyChanged(); }
        }

        public bool IsCompleted
        {
            get { return isCompleted; }
            set { isCompleted = value; OnPropertyChanged(); }
        }

        public DateTime Date
        {
            get { return date; }
            set { date = value; OnPropertyChanged(); }
        }

        public string Details
        {
            get { return details; }
            set { details = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDetails)); }
        }

        public bool HasDetails
        {
            get { return !string.IsNullOrEmpty(details); }
        }

        public bool ShowDetails
        {
            get { return showDetails; }
            set { showDetails = value; OnPropertyChanged(); }
        }

        public bool IsFirst
        {
            get { return isFirst; }
            set { isFirst = value; OnPropertyChanged(); }
        }

        public bool IsLast
        {
            get { return isLast; }
            set { isLast = value; OnPropertyChanged(); }
        }

        public bool IsDragSource
        {
            get { return isDragSource; }
            set { isDragSource = value; OnPropertyChanged(); }
        }

        public bool ShowDropBefore
        {
            get { return showDropBefore; }
            set { showDropBefore = value; OnPropertyChanged(); }
        }

        public bool ShowDropAfter
        {
            get { return showDropAfter; }
            set { showDropAfter = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class AIPrompt : INotifyPropertyChanged
    {
        private string name;
        private string prompt;
        private string color;

        public string Name
        {
            get { return name; }
            set { name = value; OnPropertyChanged(); }
        }

        public string Prompt
        {
            get { return prompt; }
            set { prompt = value; OnPropertyChanged(); }
        }

        public string Color
        {
            get { return color; }
            set { color = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class BooleanToVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                if (parameter?.ToString() == "Not")
                {
                    boolValue = !boolValue;
                }
                return boolValue ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }
            return System.Windows.Visibility.Collapsed;
        }

        public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is System.Windows.Visibility visibility)
            {
                bool result = visibility == System.Windows.Visibility.Visible;
                if (parameter?.ToString() == "Not")
                {
                    result = !result;
                }
                return result;
            }
            return false;
        }
    }
}
