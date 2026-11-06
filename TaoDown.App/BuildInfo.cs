namespace TaoDown.App;

/// <summary>
/// 构建标记（v2.58 引入）：随每次交付包手工递增，启动日志必写一行，
/// 让真机日志能自证「跑的到底是哪个构建」——此前 v33/v34 修复是否真正
/// 装到用户机器上只能靠猜，两轮排障都耗在「包到底装没装上」上。
/// </summary>
internal static class BuildInfo
{
    public const string Mark = "v1.02-1106";
}
