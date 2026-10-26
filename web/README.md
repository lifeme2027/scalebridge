# ScaleBridge Web 版

静态单页（原生 ES 模块，**零运行时依赖**，没有 Web Worker），用浏览器的 Web Bluetooth 接收标准体重秤数据，数据只存在本浏览器的 IndexedDB 里。
目标部署位置：`https://life.kastop.com/box`（`/box` 与 `/box/` 都能打开）。

> **诚实声明**
> * 真实蓝牙路径一律是 **「实验性（仅按规范实现，未真机验证）」**：没有在任何真实体重秤上测试过；CI 与本地只用字节向量、假 GATT 和 Chrome 的蓝牙模拟。
> * 界面里的「演示数据」按钮产生的是 **「演示数据」**，每一行和每次导出都带标记（导出带 `demo` 标志），不是真实体重。
> * 厂商私有协议（广播帧类，如各品牌的小米式广播）**暂未支持**；Web 版只解析标准 GATT 的体重秤 0x181D（0x2A9E / 0x2A9D）和人体成分 0x181B（0x2A9B / 0x2A9C）。

## 功能

* 点击「连接」→ 浏览器的蓝牙选择器（`requestDevice`，只在真实点击里触发，不会自动弹出）→ GATT 连接 → 读特征（Feature）→ 订阅 0x2A9D / 0x2A9C 通知。
* 显示原始单位与千克、BMI / 体脂等字段、设备时间戳；断开 / 重连 / 出错都有提示；连接与服务发现有超时看门狗（见下）。
* 历史列表、设置（用户编号备注、显示单位）、CSV / JSON 导出与导入（粘贴或选文件）、十六进制调试面板（可选）。
* 不支持 Web Bluetooth 的浏览器：友好提示 + 演示数据模式 + 导入导出，页面其余功能照常。
* 中文 / English 切换，默认按浏览器语言；所有文字来自 `src/i18n/*.js`，测试保证两份字典的键完全一致。

数据管线策略与桌面版一致（规则见仓库根的 [NOTES.md](../NOTES.md)「解析策略与已知分歧」）：体重恰为 0 不自动保存；小于 2 kg 或大于 200 kg 保存但标「异常」；
设备时间比接收时晚超过 24 小时或早超过 366 天标「设备时钟可疑」；同一设备时间戳与同样数值的记录不重复保存（没有设备时间戳就不去重）。

## 兼容性与限制（页面里也有同样的说明）

* 只有 **Chromium 系浏览器**（Chrome / Edge 等，桌面与安卓）有 Web Bluetooth；Firefox 与 Safari 没有。
* 必须 **HTTPS**（或 localhost），并且必须由**用户点击**触发选择器。
* 只能访问 `optionalServices` 里声明的服务：0x181D、0x181B、0x180A。
* 很多厂商的秤只发广播、不提供标准 GATT 服务，需要 `watchAdvertisements`，它的可用性因浏览器/平台而异；厂商广播协议暂未支持。
* 页面会做特性检测：`'bluetooth' in navigator`、`BluetoothDevice.prototype.watchAdvertisements`、`navigator.bluetooth.getAvailability()`。
* Linux 上的 Chrome（实测 156）默认没有 `navigator.bluetooth`，需用 `--enable-features=WebBluetooth` 启动；没开时站点发送的 `Permissions-Policy: bluetooth=(self)` 会在控制台出现一条无害的 "Unrecognized feature: 'bluetooth'" 提示。
* 清除浏览器数据会丢失历史记录，请定期导出备份。

## 隐私

* 数据只保存在本浏览器的 IndexedDB 里，**不会上传**；页面不向任何地方发 `fetch` / XHR / `sendBeacon`（CSP 里 `connect-src 'self'` 也只允许本站，且代码里没有任何数据接口调用）。
* 普通的服务器访问日志会记录对本页面的请求（时间、路径、浏览器标识等），其中**没有任何体重数据**。
* 没有云同步、没有账号、没有统计。完整说明见页面里的「隐私」页（`/box/privacy.html`）。

## 目录

```
src/core/      纯 JS 核心（无 DOM/浏览器依赖，可在 Node 里单测）：decimal / bytes / protocol / measurement / policy / export / import / store / pipeline / detect / fake / view
src/core/ble.js 连接控制器（Web Bluetooth 的唯一入口，含超时看门狗与重连）
src/main.js    页面装配；src/index.html、privacy.html、style.css、i18n/{zh,en}.js
scripts/       build.mjs（内容哈希构建）、serve.mjs（本地预览，带与线上一致的头）、sync-to-site.mjs、headers.mjs
tools/         parse-cli.mjs（命令行解析，交叉比对用）
test/ testlib/ node:test 单测（读取仓库根的 ../shared-vectors/*.json，不复制）
```

## 开发

```bash
cd web
npm ci
npm test                       # 全部单测（含 shared-vectors 全部标准向量）
npm run build                  # 输出 dist/（文件名带内容哈希，import 已改写）
npm run serve -- --port 18080  # 预览 http://127.0.0.1:18080/box/（带 CSP 与 Permissions-Policy）
```

CI：`.github/workflows/web.yml`（独立于 .NET 的 ci.yml）：`npm ci`、`npm test`、`npm run build`。

### 同步到站点

站点是 ASP.NET Core 项目 composehk/consumption-tracker-web，静态文件放在 `src/ConsumptionTracker.Web/wwwroot/box`（提交进该仓库，这样它的 CI 能过、发布包自带 /box）：

```bash
node scripts/sync-to-site.mjs <站点仓库路径>            # 构建并覆盖 wwwroot/box
node scripts/sync-to-site.mjs <站点仓库路径> --check    # 只比较，不一致退出码 1（站点仓库的 CI 可用）
```

站点侧为 `/box` 单独加响应头（其它路径不变）：

```
Content-Security-Policy: default-src 'self'; script-src 'self'; connect-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'
Permissions-Policy: bluetooth=(self), camera=(), microphone=(), geolocation=(), payment=(), usb=()
```

约束：没有内联脚本、没有内联事件处理器、没有 `<base>`（`base-uri 'none'`，所以资源用绝对路径 `/box/…`）、**不使用 Web Worker**（站点 CSP 没有 `worker-src`；若以后确实需要，只在 /box 的 CSP 里加 `worker-src 'self'`）。
缓存：`index.html` 等不带哈希的文件 `no-cache`；`*.<10 位哈希>.js|css` 可缓存一年（immutable）。

## 解析命令行（与桌面 ParseCli 交叉比对）

`tools/parse-cli.mjs` 的参数与桌面 `tools/ParseCli` 一致，输出与 `shared-vectors` 的向量同形（`reject` / `errorCode` / `warnings` / `expect`，十进制值是字符串）。同样是「实验性（仅按规范实现，未真机验证）」。

```bash
# 单条
node tools/parse-cli.mjs --service 181D --char 2A9D --hex "00 59 36"

# 标准输入，每行一条（"服务/特征 字节"，或用 --service/--char 给默认值）；空行与 # 开头的行忽略；输出 JSON Lines
printf '181D/2A9D 00 59 36\n181B/2A9C 00\n' | node tools/parse-cli.mjs --stdin

# 批量读 scalebridge-vectors/1（--file 可重复，别名 --vectors；--dir 读目录顶层的 *.json）
node tools/parse-cli.mjs --dir ../shared-vectors --check     # --check（别名 --compare）与文件期望比较，不一致退出码 1
node tools/parse-cli.mjs --file their-vectors.json > actual.json
```

* 单条与 stdin 的输出对象含 `service` / `characteristic` / `hex`（大写、空格分隔），与桌面版逐字节一致（对同一输入）。
* **厂商广播向量**（`shared-vectors/vendor/*`，带 `frameType`）Web 版不实现：批量输出 `{"name":…,"unsupported":true,…}`，`--check` 时计入 skipped，不算不一致。所以和桌面交叉比对时只覆盖标准 GATT 向量。
* 退出码：`0` 正常，`1` `--check` 有不一致，`2` 参数错误 / 读文件失败 / stdin 有坏行（坏行输出 `{"line":N,"cliError":…}`）。
* 失败时 `errorCode` 与桌面的枚举名相同（如 `TooShort`、`MultiPacketNotSupported`）；Web 版不输出桌面附带的 `errorMessage`。

## 给蓝牙模拟测试的钩子

页面关键元素都有稳定的 `id` 和 `data-testid`（两者同名）：

| 元素 | testid | 说明 |
| --- | --- | --- |
| 连接 / 重连 / 断开 | `btn-connect` / `btn-reconnect` / `btn-disconnect` | 选择器只由 `btn-connect` 的真实点击触发 |
| 连接状态 | `conn-state` | `data-state`：`idle` / `requesting` / `connecting` / `connected` / `reconnecting` / `disconnected` |
| 浏览器支持提示 | `support-msg` | `data-level`：`ok` / `noApi` / `insecure` / `noAdapter` |
| 错误提示 | `conn-error` | `data-code`：如 `timeout`、`cancelled`、`noDevice` |
| 最新体重 | `latest-weight` | `data-kg`、`data-demo` |
| 历史 | `history-table`、`history-rows`、行 `history-row`、`history-weight`、`history-delete` | 行上有 `data-id`、`data-kg`、`data-demo` |
| 演示按钮 | `demo-<场景>`（容器 `demo-buttons`） | 场景：`si-basic` / `si-full` / `imperial` / `body-composition` |

超时看门狗：`requestDevice` 之后的每一步 GATT 操作（`connect`、`getPrimaryService`、`getCharacteristic`、`startNotifications` 等）各有 9 秒超时（读 Feature 等可选步骤 3 秒），
超时后显示友好提示（`data-code="timeout"`）并允许重试；重连最多 3 次，之后提示「连接已丢失」。对应单测在 `test/ble.test.js`。

Chrome 蓝牙模拟（CDP `BluetoothEmulation`）的已知坑见内部测试报告：GATT 错误码会让调用一直挂起（所以要看门狗）、每个特征要有 CCCD 描述符、同一选择器不要选两次。
