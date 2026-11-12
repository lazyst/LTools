using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Features.Builtin;

namespace LTools.Views;

/// <summary>
/// 内置功能的参数表单：按 <see cref="FeatureParam"/> 声明自动生成（文本 / 数字 / 下拉 / 屏幕区域）。
/// </summary>
/// <remarks>
/// 只在「放参数」时用得到——功能本身的引用就写在面板槽位 / 快捷键 / 悬浮的字符串里
/// （<c>common.capture#source=region&amp;region=…</c>），确定后由调用方替换那一处引用。
/// </remarks>
public partial class FeatureParamsDialog : Window
{
    private readonly FeatureDef _def;
    private readonly Dictionary<string, string> _initial;
    private readonly List<Row> _rows = new();

    private sealed class Row
    {
        public required FeatureParam Param { get; init; }
        public required FrameworkElement Host { get; init; }
        public required Func<string> Read { get; init; }
        public required Action<string> Write { get; init; }
    }

    private FeatureParamsDialog(FeatureDef def, string reference)
    {
        InitializeComponent();
        _def = def;
        _initial = new Dictionary<string, string>(def.Resolve(FeatureRef.Parse(reference)),
            StringComparer.Ordinal);

        Title = def.Name + " · 参数";
        Headline.Text = def.Name;
        BuildForm();
        UpdateVisibility();
    }

    /// <summary>弹出参数表单；确定时返回新的功能引用，取消返回 null。</summary>
    public static string? Show(Window? owner, string reference)
    {
        var def = FeatureCatalog.Find(FeatureRef.IdOf(reference));
        if (def == null || !def.HasParams) return null;

        var dialog = new FeatureParamsDialog(def, reference)
        {
            Owner = owner,
            Topmost = owner == null,
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <summary>确定后的新引用（取消时为 null）。</summary>
    public string? Result { get; private set; }

    private void BuildForm()
    {
        foreach (var param in _def.Params)
        {
            var host = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            host.Children.Add(new TextBlock
            {
                Text = param.Label,
                Style = (Style)FindResource("SectionLabel"),
                Margin = new Thickness(0, 0, 0, 4),
            });

            switch (param.Kind)
            {
                case FeatureParamKind.Choice:
                {
                    var box = new ComboBox { MinHeight = 30 };
                    foreach (var option in param.Options ?? Array.Empty<FeatureOption>())
                        box.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
                    SelectChoice(box, _initial.TryGetValue(param.Key, out var v) ? v : param.Default);
                    box.SelectionChanged += (_, _) => UpdateVisibility();
                    host.Children.Add(box);
                    _rows.Add(new Row
                    {
                        Param = param,
                        Host = host,
                        Read = () => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? param.Default,
                        Write = value => SelectChoice(box, value),
                    });
                    break;
                }

                case FeatureParamKind.Region:
                {
                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var box = new TextBox { MinHeight = 30 };
                    box.Text = _initial.TryGetValue(param.Key, out var r) ? r : param.Default;
                    grid.Children.Add(box);

                    var pick = new Button
                    {
                        Content = "框选…",
                        Style = (Style)FindResource("BtnGhost"),
                        MinWidth = 70,
                        Margin = new Thickness(8, 0, 0, 0),
                    };
                    Grid.SetColumn(pick, 1);
                    pick.Click += (_, _) =>
                    {
                        if (ScreenPicker.TryPickRegion(this, out Int32Rect area, out _))
                        {
                            box.Text = string.Format(CultureInfo.InvariantCulture,
                                "{0},{1},{2},{3}", area.X, area.Y, area.Width, area.Height);
                        }
                    };
                    grid.Children.Add(pick);

                    host.Children.Add(grid);
                    _rows.Add(new Row
                    {
                        Param = param,
                        Host = host,
                        Read = () => box.Text.Trim(),
                        Write = value => box.Text = value,
                    });
                    break;
                }

                default:
                {
                    var box = new TextBox { MinHeight = 30 };
                    box.Text = _initial.TryGetValue(param.Key, out var t) ? t : param.Default;
                    if (param.Kind == FeatureParamKind.Number)
                        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
                    host.Children.Add(box);
                    _rows.Add(new Row
                    {
                        Param = param,
                        Host = host,
                        Read = () => box.Text.Trim(),
                        Write = value => box.Text = value,
                    });
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(param.Hint))
            {
                host.Children.Add(new TextBlock
                {
                    Text = param.Hint,
                    Style = (Style)FindResource("HintLabel"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0),
                });
            }

            FormHost.Children.Add(host);
        }

        if (!string.IsNullOrWhiteSpace(_def.Description))
        {
            FormHost.Children.Add(new TextBlock
            {
                Text = _def.Description,
                Style = (Style)FindResource("HintLabel"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
    }

    private static void SelectChoice(ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, value, StringComparison.Ordinal))
            {
                box.SelectedItem = item;
                return;
            }
        }
        // 旧值 / 未知值：原样保留成一项，别静默落到第一项把配置改掉
        if (value.Length > 0)
        {
            var custom = new ComboBoxItem { Content = value + "（未知）", Tag = value };
            box.Items.Add(custom);
            box.SelectedItem = custom;
        }
        else if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    /// <summary>按「比较方式」这类联动条件收起无关的参数行。</summary>
    private void UpdateVisibility()
    {
        var values = CurrentValues();
        foreach (var row in _rows)
            row.Host.Visibility = row.Param.IsVisible(values) ? Visibility.Visible : Visibility.Collapsed;
    }

    private Dictionary<string, string> CurrentValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in _rows) values[row.Param.Key] = row.Read();
        return values;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            if (!row.Param.IsVisible(CurrentValues())) continue;

            string value = row.Read();
            if (row.Param.Kind == FeatureParamKind.Number)
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    Warn($"「{row.Param.Label}」要填数字。");
                    return;
                }
            }
            else if (row.Param.Kind == FeatureParamKind.Region && value.Length == 0)
            {
                Warn($"「{row.Param.Label}」还没填，点「框选…」选一块区域。");
                return;
            }

            values[row.Param.Key] = value;
        }

        Result = FeatureRef.Build(_def.Id, values);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Warn(string message) => ConfirmDialog.Info(this, "功能参数", message);
}
