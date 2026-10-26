using System.Runtime.CompilerServices;

// 原生句柄访问已收敛为 internal（P1.6 评审 R6：Engine 不向应用层暴露 IntPtr）。
// P1 探针与单元测试属白盒验证，保留内部可见性。
[assembly: InternalsVisibleTo("MaaYys.P1Probe")]
[assembly: InternalsVisibleTo("MaaYys.Engine.Tests")]
[assembly: InternalsVisibleTo("MaaYys.App")]
