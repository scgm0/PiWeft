namespace PiWeft.Desktop;

public static partial class Strings
{
	public const string ImagePreviewOpen = "点击放大查看图片";
	public const string AlertNote = "说明";
	public const string AlertTip = "提示";
	public const string AlertImportant = "重要";
	public const string AlertWarning = "警告";
	public const string AlertCaution = "危险";
	public const string ImagePreviewClose = "关闭图片预览";
	public const string AttachFiles = "添加文件或图片";
	public const string AllFiles = "所有文件";
	public const string SendOptions = "发送选项";
	// 发送弹窗：标题只写动作，协议名与生效时机放在第二行（PiDeck 的 Split Send 同款）。
	public const string SteerCurrentTurn = "加入当前回合";
	public const string SteerCurrentTurnHint = "steer · 下一次模型调用前生效";
	public const string QueueNextTurn = "排队到下一轮";
	public const string QueueNextTurnHint = "followUp · 当前回复结束后发送";
	public const string AbortHint = "停止生成，已生成的部分保留";
	public const string SteerQueued = "已加入当前回合，下一次模型调用前生效。";
	public const string FollowUpQueued = "已排队到下一轮，当前回复结束后发送。";
	public const string AttachmentLimit = "图片限单张 10 MiB、总计 20 MiB，最多 10 张。";
	public const string ImageModelRequired = "当前模型不支持图片，请选择支持图片的模型后发送。";
	public const string AttachmentReadFailed = "部分附件无法读取，或超过大小上限。";
	public const string AttachmentLoading = "正在读取图片，请稍后发送。";
}
