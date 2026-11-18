# 部署侧审计：路由规则、systemd 单元、临时文件与代码同步

日期：2026-11-18
范围：本机 JYSZ（境内客户端机）与 HND0（`45.11.1.62` / `hnd0.kw92.cyou`，服务端）。
对应 TODO：P2-12、P2-13；P2-10 的对比方法见文末。

## 1. 路由绕过规则（P2-12）

### 现状

本机 `ip rule show` 的相关项：

```
8890:	from all to 45.11.1.62 lookup main
8900:	from all uidrange 954-954 lookup main
```

`ip route get 45.11.1.62`：

```
45.11.1.62 via 172.24.0.1 dev enp8s0 src 172.24.14.184 uid 0
```

承载地址确实从 `enp8s0` 出，没有进 mihomo 的 2022 表。

HND0 的 `ip rule` 只有 `15: from 45.11.1.62 lookup main`，没有 8890；
服务端不需要客户端那套绕过规则。

### 幂等性实测

用另一个优先级做重复添加，避免动到 8890：

```
ip rule add pref 8899 to 45.11.1.62 lookup main   # rc=0
ip rule add pref 8899 to 45.11.1.62 lookup main   # rc=2, RTNETLINK answers: File exists
ip rule del  pref 8899                            # rc=0
ip rule del  pref 8899                            # rc=2, RTNETLINK answers: No such file or directory
```

结论：`ip rule add` 本身**不幂等**，重复执行会返回非零。单元里的
`ExecStartPre=-/bin/sh -c "/sbin/ip rule del pref 8890; /sbin/ip rule add pref 8890 ..."`
依靠 `-` 前缀忽略失败，因此服务重启是安全的；删除失败（规则本来就不存在）也不会阻止 `add`。

一个已知副作用：`ip rule del pref 8890` 会删掉任何同优先级的规则，不只本服务加的那条。
本机只有这一条 8890，可以接受；多实例部署时需要改成带 `to 45.11.1.62/32` 的精确删除。

### 重启后的持久性

- 系统重启：systemd 启动 `tbtun-hnd0.service` 时重跑 `ExecStartPre`，规则恢复。
- mihomo 重启：不影响 tbtun 的规则，因为规则不归 mihomo 管。
- 只重启 `mihomo.service` 后应复查 `ip rule show | grep 8890` 与 `ip route get 45.11.1.62`。

未在本机执行整机重启（会中断当前隧道与其它使用者），因此“重启后仍指向 `enp8s0`”
目前只有上面两条机制性证据，没有重启实测。

## 2. systemd 单元

### 本机（JYSZ，客户端）

`/etc/systemd/system/tbtun-hnd0.service`：

```
ExecStartPre=-/bin/sh -c "/sbin/ip rule del pref 8890; /sbin/ip rule add pref 8890 to 45.11.1.62/32 lookup main"
ExecStart=/usr/local/bin/tbtun client --server 45.11.1.62:39443 --listen 127.0.0.1:18443 --psk-file /etc/tbtun/psk --paths 256
Restart=always
LimitNOFILE=1048576
```

### HND0（服务端）

`/etc/systemd/system/tbtun-hnd0.service` 加 drop-in `stats.conf`：

```
ExecStart=/usr/local/bin/tbtun server --listen 0.0.0.0:39443 --target 127.0.0.1:18443 --psk-file /etc/tbtun/psk --max-paths 256 --stats-s 1
```

没有 `ExecStartPre`，也没有 ip rule 依赖。`systemctl list-unit-files` 显示
`tbtun-hnd0.service enabled`。

## 3. 临时文件与备份（P2-13）

### `/run/hnd0tbt4.yaml`、`.pid`、`.log`（本机）

**仍在使用，不能删除。**

```
/run/hnd0tbt4.pid  -> 4058250
4058250 /opt/mihomo/mihomo -f /run/hnd0tbt4.yaml -d /run
```

`/run/hnd0tbt4.yaml` 是另一份 mihomo 配置：`mixed-port: 17893`，代理 `HND0TBT4` 指向
`127.0.0.1:18443` 的 vless（`network: tcp`、`tls: false`、`smux.enabled: true`）。
它和 systemd 的 `mihomo.service` 并存。需要运维确认这是否是有意的第二实例；
在确认之前不要删除这三个 `/run` 文件。

### `/opt/mihomo/config.yaml.bak-smux1`（本机）

存在，12220 字节，mtime 2026-11-17 17:20；同目录还有 `config.yaml.bak-hnd0tbt4`
（11919 字节，17:10）。建议把 `config.yaml.bak-smux1` 保留到 **2026-12-18**，
作为 smux `max-connections: 1` / `min-streams: 1` 调整的回滚点；到期后再评估。

### HND0 上的同名文件

HND0 上不存在 `/run/hnd0tbt4.*`，也不存在 `/opt/mihomo/config.yaml.bak-smux1`。
上面的路径都属于本机 JYSZ，不要按名字去 HND0 找。

## 4. HND0 与本机代码差异

| 项 | 本机 JYSZ | HND0 |
|---|---|---|
| HEAD | `1cf420e` + 未提交改动 | `1cf420e` + 未提交改动 |
| `src/Tbt.Async` | 存在 | **不存在** |
| 未跟踪的 `AsyncMode.fs`、`docs/async-rewrite/` | 存在 | 不存在 |

HND0 的 `git status --porcelain`（第一次审计）显示 10 个修改文件：
`FlowBridge.fs`、`PathLink.fs`、`SessionCarrier.fs`、`SocketTuning.fs`、
`Client.fs`、`Server.fs`、`Decoder.fs`、`Gf16.fs`、`ParityHold.fs`、`RxHalf.fs`。

结论：**异步模式尚未部署到 HND0**。`TBTUN_ASYNC_MODE` 在 HND0 上不会生效，
因为二进制里没有 `Tbt.Async`。要在 HND0 上验证 P2-10 或异步模式，必须先同步
`src/Tbt.Async`、`src/Tbt.Cli/AsyncMode.fs`、`AsyncPaths.fs` 与 fsproj 的引用，
并重新发布二进制。同步前要保留 HND0 现有的 10 个修改文件，不能直接覆盖。

### 2026-11-19 更新：源码树与运行二进制已分离

上段的“异步模式尚未部署到 HND0”只对源码树成立，对运行实例已不成立：

| 项 | 本机 JYSZ | HND0 |
|---|---|---|
| 源码 HEAD | `0568804` | `1cf420e` + 10 个未提交改动 |
| `src/Tbt.Async` | 存在 | **不存在** |
| 运行二进制 SHA-256 | `c882b2ba…` | `c882b2ba…`（相同） |
| 异步日志字段 | 有 | 有（6 小时窗口 808 行） |

部署走 `dotnet publish` 产物，不依赖 HND0 源码树；二进制内已编译 `Tbt.Async`，
异步在 HND0 上实际生效。源码树未同步只影响“在 HND0 上重新编译”，不影响运行。
若要在 HND0 上改代码重编，仍需先同步 `src/Tbt.Async`、`src/Tbt.Cli/AsyncMode.fs`、
`AsyncPaths.fs` 与 fsproj 引用，并保留现有 10 个修改文件。

## 5. P2-10 的 SSE2 对比方法

`Gf16.mulAddInto` 的分派顺序是：SSSE3/NEON → 表查；`Sse2.IsSupported` → `sse2MulAdd`；
否则标量。HND0 的 QEMU CPU 只有 SSE2，所以走 `sse2MulAdd`。

不需要改产品代码就能做启用/禁用对比：设置

```
DOTNET_EnableHWIntrinsic=0
```

让 `Sse2.IsSupported` 返回 false，同一份二进制会走标量路径。对比时同时记录：

- `--stats-s 1` 日志里的 `csvc_us`、`iter_us_max`；
- 同一负载下的吞吐与 `recovered / frames_in`；
- 两次运行必须使用同一二进制、同一 `--max-paths 256`、同一负载脚本。

该对比尚未执行；HND0 上先要完成第 4 节的代码同步。
