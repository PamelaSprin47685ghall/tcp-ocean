# Tbt.Tests 编译超时：根因与处置

更新时间：2026-11-18

## 结论

根因是 **F# 10 的 nullness 类型检查在大型测试文件上呈超线性开销**，不是文件数、引用程序集、`inline` 元数据或优化器。两个测试工程加 `<Nullable>disable</Nullable>` 后：

| 工程 | 修改前 fsc | 修改后 fsc | 工程总耗时 |
|---|---|---|---|
| `tests/Tbt.Tests`（9 文件，约 1500 行） | 377 273 ms | **4 529 ms** | 7 s |
| `tests/Tbt.Session.Tests`（24 文件，约 6300 行） | >560 s（超时） | **9 299 ms** | 12 s |
| `tests/Tbt.Async.Tests`（1 文件） | — | — | 8–30 s |

验收命令 `dotnet build Tbt.slnx -c Release -p:PublishAot=false -p:NuGetAudit=false` 在增量状态下 **EXIT=0，2 s**；单工程全量重编也远低于 300 s。

## 证据链

1. 探针工程（1 文件、1 个 `Assert.True`）：无产品引用 **924 ms**；引用 `Tbt.Crypto` **929 ms**。多一个引用只增加 5 ms。
2. Codec 组（9 文件、约 1500 行）：fsc **377 273 ms**，`Time Elapsed 00:06:19`。
3. Session 组（24 文件、约 6300 行）：560 s 超时，未出现 `Fsc` 完成行。
4. 加 `<Nullable>disable</Nullable>` 后：4 529 ms 与 9 299 ms，均 `Build succeeded`、0 Warning、0 Error。
5. 全量测试仍可运行：`Tbt.Session.Tests` 237 个用例，18 s 跑完。

## 被实验否定的假设（保留记录，避免重走）

| 假设 | 实验 | 结论 |
|---|---|---|
| `Tbt.Crypto` 的 `let inline` X25519 是主因 | 探针带/不带 `Tbt.Crypto` | 924 vs 929 ms，否定 |
| `Optimize=true` 导致优化器爆炸 | 加 `<Optimize>false</Optimize>` | fsc 仍 377 s，否定 |
| 文件数或单文件过大 | 把 `SessionTests.fs`/`TunTests.fs` 各拆 4 份 | 总 fsc 时间不变，否定 |
| 按工程拆分并并行编译即可 | 建 `Tbt.Session.Tests` 复用源文件 | 拆分后仍 >560 s，否定 |
| 64 KiB 固定流窗口导致某测试慢 | 读 `Flows.fs:186` | `Credit.Enabled=false` 时 `TxLimit=UInt64.MaxValue`，否定 |

拆文件与拆工程仍然保留：它们不减少总编译时间，但把测试按主题分组，便于单独运行与定位；且已通过 `Nullable` 修复后没有额外代价。

## 处置（已落盘）

1. `tests/Tbt.Tests/Tbt.Tests.fsproj` 与 `tests/Tbt.Session.Tests/Tbt.Session.Tests.fsproj` 各加：
   ```xml
   <Nullable>disable</Nullable>
   ```
   测试工程不需要 nullness 分析；产品库仍保持仓库默认的 nullness 设置。
2. `tests/Tbt.Tests/PollerTests.fs:14`：`:?> IPEndPoint | null |> nonNull` 改为 `:?> IPEndPoint`（nullness 关闭后 FS3060）。
3. 保留 `Tbt.Probe.Tests` 与 `Tbt.ProbeCrypto.Tests` 作为回归基线，不加入 `Tbt.slnx`。
4. `Tbt.slnx` 注册 `Tbt.Tests`、`Tbt.Session.Tests`、`Tbt.Async.Tests`；`Tbt.Tests.Min` 已删除。

## 命名陷阱

`Directory.Build.props` 的 AOT 条件是 `StartsWith('Tbt.') && !EndsWith('.Tests')`。任何新测试工程必须以 `.Tests` 结尾，否则会被当成产品库，额外获得 `IsAotCompatible=true` 与 `--reflectionfree`，fsc 会做 AOT 分析。

## 测量口径

```sh
pkill -9 -x dotnet; pkill -9 -x VBCSCompiler
sleep 2
t0=$(date +%s)
timeout 400 dotnet build Tbt.slnx -c Release -p:PublishAot=false -p:NuGetAudit=false -v:m > /tmp/slnx-build.log 2>&1
echo "EXIT=$? ELAPSED=$(( $(date +%s) - t0 ))s"
```

`pkill -f` 匹配完整命令行，会杀死含同样字面的 shell 自身；用 `-x dotnet` 按进程名匹配。

## 已知边界：karnCase 的时间阈值

`Tbt.Tests.SessionTestsC.a repair stall does not inflate SRTT and slow every later repair (Karn)(loss: 0.10, blackMs: 300)` 仍差 1.9 %：

```
srtt before 42 ms, max after 44 ms, end 44 ms; 6112 ms to finish; boosters 4784;
minrtt 41; wait 43; skipped 68; repeats 59; head 800; peerAck 772; recovered 0
```

- `head = 800`：6.4 MB 在第 800 轮已全部封包。
- `peerAck = 772`：尾部 28 步未确认，约 5.3 s 花在恢复上。
- 机制：`RxHalf.TryNack` 一次只报一个缺洞，`SeenEnd < f + 66` 时依赖「20 ms 未推进」；单洞往返约 20 + 20 + 43 ms。28 个洞串行即约 3 s，叠加重复修复到 5.3 s。
- SRTT 本身已修好（123 ms → 44 ms），`maxSrtt < 4 × minRTT` 通过。`r` 在四种采样实现下恒为 6112，与 Karn 采样无关。
- 曾尝试在 `repairCount ≥ StepUsed` 时提前由 BOOSTER 改发 DATA，可使该测试变快，但破坏 `UniqueRepairTests` 的既有契约（`Expected: 256, Actual: 4`），已回滚。
- 合法修法是让 `TryNack` 批量报洞（或把 `SeenEnd` 判据从 66 降到 4）。这是「修复与 RTO 兜底」敏感默认，必须同时跑全量单测、4 路 e2e 的 `rtt_skipped`/`recovered`/`spurious`、单流 5×15 s 与 3 流矩阵后才能改。
