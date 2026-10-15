using Aprillz.MewUI;

namespace PiWeft.Desktop;

/// <summary>
/// MewUI 自己绘制的界面文案（消息框、输入框右键菜单、内置文件对话框、忙碌指示器、颜色选择器）
/// 默认是英文，这里统一改成中文。字符串以 <see cref="ObservableValue{T}"/> 暴露，因此赋值后
/// 已经打开的编辑命令菜单会立刻更新，对话框则在下次打开时生效。
/// </summary>
/// <remarks>
/// 值里的下划线是访问键（Alt 加速键），沿用 MewUI 的约定：中文标签后跟一个带下划线的字母。
/// </remarks>
public static class MewUiLocalization
{
	/// <summary>把框架内置文案切换成中文。可在启动时调用，也可在语言变化时再次调用。</summary>
	public static void ApplyChinese()
	{
		// 通用按钮
		MewUIStrings.CommonOK.Value = "确定(_O)";
		MewUIStrings.CommonCancel.Value = "取消(_C)";
		MewUIStrings.CommonYes.Value = "是(_Y)";
		MewUIStrings.CommonNo.Value = "否(_N)";
		MewUIStrings.CommonRetry.Value = "重试(_R)";
		MewUIStrings.CommonIgnore.Value = "忽略(_I)";
		MewUIStrings.CommonAbort.Value = "中止(_A)";

		// 消息框标题与详情开关
		MewUIStrings.PromptInformation.Value = "信息";
		MewUIStrings.PromptWarning.Value = "警告";
		MewUIStrings.PromptError.Value = "错误";
		MewUIStrings.PromptQuestion.Value = "确认";
		MewUIStrings.PromptSuccess.Value = "成功";
		MewUIStrings.PromptShield.Value = "安全";
		MewUIStrings.PromptCrash.Value = "崩溃";
		MewUIStrings.PromptShowDetail.Value = "显示详细信息(_D)";

		// 忙碌指示器
		MewUIStrings.BusyIndicatorAbortConfirmation.Value = "确定要中止当前操作吗？";
		MewUIStrings.BusyIndicatorAborting.Value = "正在中止…";

		// 编辑命令（输入框右键菜单）：这些是活绑定，改完立即生效
		MewUIStrings.CommandUndo.Value = "撤销";
		MewUIStrings.CommandRedo.Value = "重做";
		MewUIStrings.CommandCut.Value = "剪切";
		MewUIStrings.CommandCopy.Value = "复制";
		MewUIStrings.CommandPaste.Value = "粘贴";
		MewUIStrings.CommandDelete.Value = "删除";
		MewUIStrings.CommandSelectAll.Value = "全选";

		// 内置文件对话框
		MewUIStrings.FileDialogTitleOpenSingle.Value = "打开文件";
		MewUIStrings.FileDialogTitleOpenMultiple.Value = "打开文件";
		MewUIStrings.FileDialogTitleSave.Value = "保存文件";
		MewUIStrings.FileDialogTitleSelectFolder.Value = "选择文件夹";
		MewUIStrings.FileDialogTitleFallback.Value = "选择";
		MewUIStrings.FileDialogAcceptOpen.Value = "打开(_O)";
		MewUIStrings.FileDialogAcceptSave.Value = "保存(_S)";
		MewUIStrings.FileDialogAcceptSelect.Value = "选择(_S)";
		MewUIStrings.FileDialogFileNameLabel.Value = "文件名(_N)";
		MewUIStrings.FileDialogFileTypeLabel.Value = "文件类型(_T)";
		MewUIStrings.FileDialogNavBack.Value = "后退";
		MewUIStrings.FileDialogNavForward.Value = "前进";
		MewUIStrings.FileDialogNavUp.Value = "上一级";
		MewUIStrings.FileDialogViewGrid.Value = "图标视图";
		MewUIStrings.FileDialogViewList.Value = "列表视图";
		MewUIStrings.FileDialogAllFiles.Value = "所有文件";
		MewUIStrings.FileDialogColumnName.Value = "名称";
		MewUIStrings.FileDialogColumnSize.Value = "大小";
		MewUIStrings.FileDialogColumnModified.Value = "修改时间";

		// 文件对话框侧栏与已知目录
		MewUIStrings.SidebarQuickAccess.Value = "快速访问";
		MewUIStrings.SidebarThisPC.Value = "此电脑";
		MewUIStrings.SidebarFavorites.Value = "收藏夹";
		MewUIStrings.SidebarLocations.Value = "位置";
		MewUIStrings.SidebarPlaces.Value = "位置";
		MewUIStrings.SidebarDevices.Value = "设备";
		MewUIStrings.FolderHome.Value = "主目录";
		MewUIStrings.FolderDesktop.Value = "桌面";
		MewUIStrings.FolderDownloads.Value = "下载";
		MewUIStrings.FolderDocuments.Value = "文档";
		MewUIStrings.FolderPictures.Value = "图片";
		MewUIStrings.FolderMusic.Value = "音乐";
		MewUIStrings.FolderVideos.Value = "视频";
		MewUIStrings.FolderApplications.Value = "应用程序";

		// 颜色选择器
		MewUIStrings.ColorPickerHex.Value = "十六进制";
	}
}
