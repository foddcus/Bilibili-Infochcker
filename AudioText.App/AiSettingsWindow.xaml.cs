using System.Windows;
using AudioText.Verification.Services;

namespace AudioText.App;

/// <summary>
/// AI/API 设置窗口交互逻辑。
/// AI/API settings window interaction logic.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public partial class AiSettingsWindow : Window
{
    private static readonly IReadOnlyList<VerificationIntensitySelectionItem> VerificationIntensityItems =
    [
        new(AiVerificationIntensity.Normal),
        new(AiVerificationIntensity.Detail),
        new(AiVerificationIntensity.Strict)
    ];

    /// <summary>
    /// 使用当前 AI 设置初始化设置窗口，避免用户每次打开都重新填写。
    /// Initialize the settings window with the current AI settings so users do not need to re-enter values.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public AiSettingsWindow(AiVerificationSettings currentSettings)
    {
        InitializeComponent();

        Settings = currentSettings ?? AiVerificationSettings.CreateDefault();
        DeepSeekModelComboBox.ItemsSource = AiVerificationSettings.SupportedModels;
        VerificationIntensityComboBox.ItemsSource = VerificationIntensityItems;
        ApplySettingsToUi(Settings);
    }

    /// <summary>
    /// 用户点击保存后返回给主窗口的 AI 设置。
    /// AI settings returned to the main window after the user saves.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public AiVerificationSettings Settings { get; private set; }

    /// <summary>
    /// 保存按钮事件：读取设置页输入并关闭窗口。
    /// Save button event: read settings-page input and close the window.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Settings = BuildSettingsFromUi();
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 恢复默认按钮事件：把控件恢复到内置默认配置。
    /// Reset button event: restore controls to built-in defaults.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        ApplySettingsToUi(AiVerificationSettings.CreateDefault());
    }

    /// <summary>
    /// 将 AI 设置对象写入界面控件。
    /// Write an AI settings object into the UI controls.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private void ApplySettingsToUi(AiVerificationSettings settings)
    {
        DeepSeekBaseUrlTextBox.Text = settings.BaseUrl;
        DeepSeekModelComboBox.Text = settings.Model;
        DeepSeekApiKeyPasswordBox.Password = settings.ApiKey;
        BochaWebSearchApiKeyPasswordBox.Password = settings.BochaWebSearchApiKey ?? string.Empty;
        SearxngEndpointTextBox.Text = settings.SearxngEndpoint ?? string.Empty;
        VerificationIntensityComboBox.SelectedItem = VerificationIntensityItems.FirstOrDefault(
            item => item.Intensity == settings.VerificationIntensity);
        BlockKuaishouCheckBox.IsChecked = settings.IsEvidencePlatformBlocked("快手");
        BlockDouyinCheckBox.IsChecked = settings.IsEvidencePlatformBlocked("抖音");
        BlockXiaohongshuCheckBox.IsChecked = settings.IsEvidencePlatformBlocked("小红书");
        BlockBilibiliCheckBox.IsChecked = settings.IsEvidencePlatformBlocked("B站");
        BlockZhihuCheckBox.IsChecked = settings.IsEvidencePlatformBlocked("知乎");
    }

    /// <summary>
    /// 从设置页控件构建 AI 设置；空值回退到默认配置，避免主流程拿到不可用空字段。
    /// Build AI settings from settings-page controls; empty values fall back to defaults so the main flow does not receive unusable fields.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private AiVerificationSettings BuildSettingsFromUi()
    {
        var apiKey = string.IsNullOrWhiteSpace(DeepSeekApiKeyPasswordBox.Password)
            ? AiVerificationSettings.DefaultApiKey
            : DeepSeekApiKeyPasswordBox.Password.Trim();
        var model = string.IsNullOrWhiteSpace(DeepSeekModelComboBox.Text)
            ? AiVerificationSettings.DefaultModel
            : DeepSeekModelComboBox.Text.Trim();
        var baseUrl = string.IsNullOrWhiteSpace(DeepSeekBaseUrlTextBox.Text)
            ? AiVerificationSettings.DefaultBaseUrl
            : DeepSeekBaseUrlTextBox.Text.Trim();
        var bochaWebSearchApiKey = string.IsNullOrWhiteSpace(BochaWebSearchApiKeyPasswordBox.Password)
            ? null
            : BochaWebSearchApiKeyPasswordBox.Password.Trim();
        var searxngEndpoint = string.IsNullOrWhiteSpace(SearxngEndpointTextBox.Text)
            ? null
            : SearxngEndpointTextBox.Text.Trim();
        var verificationIntensity = (VerificationIntensityComboBox.SelectedItem as VerificationIntensitySelectionItem)?.Intensity
            ?? AiVerificationSettings.DefaultVerificationIntensity;
        var blockedEvidencePlatformNames = BuildBlockedEvidencePlatformNamesFromUi();

        return new AiVerificationSettings(
            apiKey,
            model,
            baseUrl,
            bochaWebSearchApiKey,
            searxngEndpoint,
            verificationIntensity,
            blockedEvidencePlatformNames);
    }

    /// <summary>
    /// 从广告数据源屏蔽源复选框读取当前勾选的平台名称。
    /// Read currently checked platform names from ad/data source block checkboxes.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private IReadOnlyCollection<string> BuildBlockedEvidencePlatformNamesFromUi()
    {
        var blockedPlatformNames = new List<string>();
        AddBlockedPlatformNameIfChecked(blockedPlatformNames, BlockKuaishouCheckBox.IsChecked, "快手");
        AddBlockedPlatformNameIfChecked(blockedPlatformNames, BlockDouyinCheckBox.IsChecked, "抖音");
        AddBlockedPlatformNameIfChecked(blockedPlatformNames, BlockXiaohongshuCheckBox.IsChecked, "小红书");
        AddBlockedPlatformNameIfChecked(blockedPlatformNames, BlockBilibiliCheckBox.IsChecked, "B站");
        AddBlockedPlatformNameIfChecked(blockedPlatformNames, BlockZhihuCheckBox.IsChecked, "知乎");
        return blockedPlatformNames;
    }

    /// <summary>
    /// 若复选框被勾选，则把对应平台名称写入屏蔽列表。
    /// Add the platform name to the block list when the checkbox is checked.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static void AddBlockedPlatformNameIfChecked(
        ICollection<string> blockedPlatformNames,
        bool? isChecked,
        string platformName)
    {
        if (isChecked == true)
        {
            blockedPlatformNames.Add(platformName);
        }
    }

    /// <summary>
    /// 设置页查验力度下拉框显示项，ToString 直接返回用户可读说明。
    /// Display item for the verification-intensity ComboBox; ToString returns user-facing text.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private sealed record VerificationIntensitySelectionItem(AiVerificationIntensity Intensity)
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return $"{AiVerificationSettings.GetVerificationIntensityLabel(Intensity)} - {AiVerificationSettings.GetVerificationIntensityDescription(Intensity)}";
        }
    }
}
