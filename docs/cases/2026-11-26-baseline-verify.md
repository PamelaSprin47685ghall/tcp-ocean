# 基线验证记录：2026-11-26

## 范围

在深度重构动工前记录可复现的构建/测试基线。未运行 netns 端到端脚本，未修改源码。本文为公开仓库文件，不包含任何私人主机名、地址或端口。

## 环境

- 提交：`eb54b3233b169bbfd26156cfea128fd9bbbd9179`
- SDK：用户级路径 `~/.dotnet` 上已有 .NET `10.0.404`；无需重新安装。`dotnet --list-sdks`：`10.0.404 [/home/kunweiz/.dotnet/sdk]`
- 工作树起始状态：`## main...origin/main`，干净；构建/测试期间其他并行工作开始修改源码，结束时检出 10 个源码/测试文件变更。因此本记录是“以该提交为基础、构建/测试期间出现并行改动的时间窗基线”，不是最终重构后绿基线。

## 命令与结果

| 阶段 | 命令 | 结果 | 耗时 |
|---|---|---|---:|
| 构建 | `dotnet build Tbt.slnx -c Release` | 成功；0 warning、0 error | 29 s |
| 解决方案测试 | `dotnet test Tbt.slnx -c Release --no-build -v minimal` | 失败（1 项） | 18 s |
| 额外 Probe | `dotnet test tests/Tbt.Probe.Tests -c Release -v minimal` | 通过 1/1 | 2 s |
| 额外 ProbeCrypto | `dotnet test tests/Tbt.ProbeCrypto.Tests -c Release -v minimal` | 通过 1/1 | 4 s |
| 离线用例 | `python3 tests/e2e/test_tb_guard.py` | 通过 11/11 | <1 s |
| 离线用例 | `python3 tests/e2e/test_echo.py` | 通过 19/19 | 3 s |

## 测试细分

- `Tbt.Async.Tests`：8/8 通过。
- `Tbt.Tests`：208/208 通过。
- `Tbt.Session.Tests`：375/376 通过，1 失败。
- 解决方案测试合计：591/592 通过，1 失败。
- Probe 与 ProbeCrypto 不在 `Tbt.slnx` 中，另行显式运行；两者均通过。
- 两个离线 Python 用例均通过。

## 唯一失败

失败发生在 `Tbt.Session.Tests.dll (net10.0|x64)`，用例名：

`Tbt.Tests.BridgeTests.watch and service visit only armed and writing flows, not all 256 slots`

失败断言：

`Assert.Equal() Failure: Values differ; Expected: 0; Actual: 24`

证据来自 `tests/Tbt.Session.Tests/bin/Release/net10.0/TestResults/Tbt.Session.Tests_net10.0_x64.log`。同一次运行中还有耗时 12 s 的 `minRTT` 长测试通过；该失败极短（4 ms）。按任务约束，未修复、未重跑掩盖，原样记录。

## 结论

基线不是全绿：构建与绝大多数测试通过，但存在 1 个已记录的失败用例。后续重构不能把“全量测试通过”当作起点；该失败必须在后续阶段先判定是否与并行改动或测试接口漂移有关，再纳入验收门槛。Probe/ProbeCrypto 与两个离线 Python 用例通过，测试床静态约束未被本阶段破坏。
