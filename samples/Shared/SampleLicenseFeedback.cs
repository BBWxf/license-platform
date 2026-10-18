using System;
using Licensing.Client;

namespace Licensing.Samples
{
    internal static class SampleLicenseFeedback
    {
        internal static string Describe(LicenseResult result)
        {
            if (result.Success)
            {
                return string.Format(
                    "验证成功（{0}）。\r\n本次租约截止：{1:yyyy-MM-dd HH:mm:ss} UTC。\r\n这是本次离线租约期限，不是卡密的总到期时间。可以清空卡密直接再次验证。",
                    result.UsedOfflineFallback ? "有效签名离线回退" : "在线",
                    result.Lease!.ExpiresAtUtc.UtcDateTime);
            }

            var code = result.Error?.Code ?? LicenseErrorCode.ClientFailure;
            string message;
            switch (code)
            {
                case LicenseErrorCode.ActivationRequired:
                    message = "此安装尚未激活，请输入该产品的一张测试卡密，再点击“激活 / 验证”。";
                    break;
                case LicenseErrorCode.OperationCancelled:
                    message = "操作已取消，可以重试。";
                    break;
                case LicenseErrorCode.NetworkUnavailable:
                    message = "无法连接授权服务，或请求已超时。请检查服务地址、HTTPS、网络和服务器状态。没有可用的有效离线租约，不能放行。";
                    break;
                case LicenseErrorCode.BusinessRejected:
                    message = "服务器拒绝了授权。请检查卡密所属产品、禁用/到期状态及设备绑定；需要换机时先由管理员解绑。";
                    break;
                case LicenseErrorCode.StorageFailure:
                    message = "无法读取或保存当前 Windows 用户的受保护授权状态。请检查权限及是否有另一实例正在操作，不要随意删除安装身份。";
                    break;
                case LicenseErrorCode.FingerprintUnavailable:
                    message = "无法取得设备指纹，请检查本机系统服务后重试。不要把原始硬件标识发到日志或聊天中。";
                    break;
                case LicenseErrorCode.UnknownSigningKey:
                case LicenseErrorCode.InvalidServerSignature:
                case LicenseErrorCode.UnsupportedAlgorithm:
                    message = "签名验证失败，请从可信渠道核对当前产品的公钥配置；不要关闭验签或忽略证书错误。";
                    break;
                case LicenseErrorCode.InvalidConfiguration:
                    message = "客户端配置无效。请重新导入该产品的客户端配置，并核对服务根地址。";
                    break;
                case LicenseErrorCode.TrustedTimeRollback:
                    message = "检测到可信时间异常，请校准系统时间并联网验证。";
                    break;
                default:
                    message = "授权操作未成功，请核对配置和网络后重试。此示例不会因为错误自动放行。";
                    break;
            }

            return message + Environment.NewLine + "错误码：" + code;
        }
    }
}
