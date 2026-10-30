using System.Reflection;
using RdpB.App.Services;
using RdpB.Core.Transport;

namespace RdpB.Tests;

/// <summary>對應 Kyra 實測抓到的真坑:連線測試原本寫死對 3389 測 TCP,但 Host 自己
/// 開的是 7443——兩個數字對不上,3389 剛好是標準 RDP 埠,連線測試可能誤判連到對方機器
/// 真的 Windows RDP 服務。現在兩邊都從同一個常數拿。
///
/// 這裡故意不用真的 port:0 以外的埠去 bind——7443 是正式會用的埠,平行跑測試或
/// Kyra 自己剛好開著 Host 時都可能撞號,改用反射讀方法的預設參數值,不碰網路。</summary>
public class DefaultPortsTests
{
    [Fact]
    public void HostListenerService_Start_DefaultParameterValue_MatchesSharedConstant()
    {
        var method = typeof(HostListenerService).GetMethod(nameof(HostListenerService.Start))!;
        var portParam = method.GetParameters().Single(p => p.Name == "port");

        Assert.Equal(DefaultPorts.Host, portParam.DefaultValue);
    }
}
