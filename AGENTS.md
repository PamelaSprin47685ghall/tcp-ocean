# 给在本仓库工作的代理

本仓库是 TBTun v2.2 的 F# 实现。公开远端：https://github.com/PamelaSprin47685ghall/tcp-ocean。权威规范是 `docs/spec/TBTun-主文档-v2.2.md`。规范未写死、两端必须一致的细则在 `docs/DECISIONS.md`。进度与 A/B 记录在 `docs/t9u-status.md` 和 `docs/cases/`。

## 构建与测试

```sh
dotnet build Tbt.slnx -c Release
dotnet test Tbt.slnx -c Release
python3 tests/e2e/test_tb_guard.py
python3 tests/e2e/test_echo.py
```

`TreatWarningsAsErrors` 已打开。全量单测，不要依赖会被测试宿主忽略的过滤器。不要削弱现有 no-SACK、blackout、RepairWait 等断言。`NewRepair` 保持默认关闭。

官方端到端：`tests/e2e/run.sh [owd_ms] [rate_mbit] [loss] [paths] [seconds]`。脚本自己会 `flock /tmp/tbtun-netns.lock`，不要再在外面套一层。并发者在锁上等待，不要删除别人的 netns，不要杀你没启动的测试床进程。正在跑 mux 时不要编译或修改 `run.sh` / `netns.sh`。

常用例子：

- `tests/e2e/run.sh 20 200 0 4 10`
- `tests/e2e/run.sh 20 200 0.05 4 10`（5% 必须是 `0.05`，不是 `5`）
- `PROFILE=rtt400 HANDSHAKE_S=60 tests/e2e/run.sh 200 1000 0 8 10`
- `PROFILE=rtt400 HANDSHAKE_S=60 tests/e2e/run.sh 200 1000 0 64 10`
- `PROFILE=asym20_200 tests/e2e/run.sh 20 200 0 4 10`

官方对照用干净树（或显式 `ALLOW_DIRTY=1`）。比较产品二进制时设 `TBT_BIN`，并同时记录 script commit 与二进制 hash。合入触及数据路径的改动前，bulk/iperf 不得差于当时主线，交互只需可用（负载下 echo/ping 成功、准时、没有数秒级卡住）。单枪低于主线最小不单独否决；看中位和是否稳定地板下降。与主机暂停或日志缺口重叠的测量段无效。

## 产品约束

- 客户端永远是未修改的内核 TCP，不应让它感知丢包。
- 不要写死速率或按线路写常数。上下行（以及每个路径组）各自维护带宽估计，禁止交叉拷贝。
- 不要为跨流公平或让路做设计。
- 产品面向文档（README、本文件、提交说明）不要写上游代理的具体实现名；规范原文保持不动。

## 不要轻易改的默认

速率组的 startup / drain / 满管规则、信用与 FLOW_STATE 会计、修复与 RTO 兜底、客户端路径选择与写门控，以及测试床有效性判定，都是敏感默认。改之前要有机制说明、单测，以及官方矩阵的有效样本对照。不要用放宽测试或单次幸运枪来定默认参数。
