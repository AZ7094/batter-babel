# MiSans 字体许可与合规说明

> 适用对象：Batter Babel（闭源、可商业分发的 Windows 桌面应用）
> 本次核查日期：**2026-10-19**（Asia/Shanghai）
> 核查方式：抓取小米官方页面原文（非二手转述），逐字记录关键句与 URL
> 结论仅对本次抓取到的许可版本有效——该许可明确写为「**可撤销（revocable）**」，官方可随时变更条款。

## 一句话结论

**允许**随闭源商业桌面应用以 woff2/webfont 形式嵌入分发，但**必须**满足：①在软件中（终端用户可见处）特别注明「使用了 MiSans 字体」；②不得把字体文件**单独**对外出租、再许可、出借、再分发或售卖；③不得改编/二次开发字体（含改字形外观、改字体名）。**不禁止**把字体随应用安装包整体分发。

## 逐项结论

| # | 问题 | 结论 | 依据 |
|---|---|---|---|
| 1 | 闭源商业软件里用（免费商用） | ✅ 允许 | 官方 FAQ：「所有字体都是供全球免费商用，您可以在任何平台、任何商业项目中使用」 |
| 2 | 随安装包分发 / 内嵌进应用资源 | ✅ 允许（作为应用整体） | 官方 FAQ：「是否可以用作嵌入式字体？**可以**。但您应在软件中特别注明使用了 MiSans 字体。」许可第 3 条只禁「**单独**」分发字体软件，且明说「如您使用 MiSans 字体创作……**应用 App** 等，您有权分发或出售该作品」 |
| 3 | 转 woff2 / 子集化（只删未用字符） | ⚠️ 灰区（官方无逐字许可） | 全站无「woff2 / webfont / 子集化 / 格式转换」字样。禁的是「改编或二次开发」与「单独……外观上的更改」；无损格式转换 + 不动字形轮廓与字符集裁剪，不改变外观，倾向可行，但**无原文可引** |
| 4 | 改字形外观 / 改字体名 / 二次开发 | ❌ 禁止 | 许可第 2 条：「您不得对 MiSans 字体或其任何单独组件进行改编或二次开发。」FAQ：「您不得单独将 MiSans 字体或其组件进行外观上的更改。」 |
| 5 | 单独分发字体文件（下载站、公开目录、字体包） | ❌ 禁止 | 许可第 3 条：「您不得**单独**将 MiSans 字体或其组件对外租赁、再许可、给予、出借或进一步分发字体软件或其任何副本以及重新分发或售卖。」 |
| 6 | 署名 / 版权声明 | ✅ **必须做** | 许可第 1 条：「您应在软件中特别注明使用了 MiSans 字体。」英文版：`You should specifically note in the software that MiSans Fonts was used.` |
| 7 | 必须随包附带许可全文？ | ⭕ 未强制 | 许可正文无「必须附许可副本」条款；随包附带属**建议**（低成本、利于留证），不附不构成违约 |

## 官方原文（关键句，逐字）

许可全文见同目录 [`LICENSE-MiSans.txt`](./LICENSE-MiSans.txt)。

来源 1：<https://hyperos.mi.com/font/download>（HTTP 200，抓取 2026-10-19 17:22 +08:00）

> 根据本协议的条款和条件，许可方在此授予您一份不可转让的、非独占的、免版税的、**可撤销的**、全球性的版权许可，使您依照本协议约定使用 MiSans 字体，前提是符合下列条件：
> 1. 您应在软件中特别注明使用了 MiSans 字体。
> 2. 您不得对 MiSans 字体或其任何单独组件进行改编或二次开发。
> 3. 您不得单独将 MiSans 字体或其组件对外租赁、再许可、给予、出借或进一步分发字体软件或其任何副本以及重新分发或售卖。此限制不适用于您使用 MiSans 字体创作的任何其他作品。如您使用 MiSans 字体创作宣传素材、logo、应用 App 等，您有权分发或出售该作品。

来源 2：<https://hyperos.mi.com/font/faq>（HTTP 200，抓取当天）

> 是否可以用作嵌入式字体？可以。但您应在软件中特别注明使用了 MiSans 字体。
>
> 不需要。MiSans Global 所有的字体都是供全球免费商用，您可以在任何平台、任何商业项目中使用所有字体。

来源 3（英文版，措辞与中文版有差异，见「风险」）：<https://hyperos.mi.com/font/en/download/>

> You should specifically note in the software that MiSans Fonts was used.
> You may not adapt or redevelop MiSans Fonts or any of its components.
> You may not separately lease, sublicense, give, loan or further distribute MiSans Fonts or its components; nor may you redistribute or sell the font software or any copies thereof.

## 署名要求（这是唯一硬性附加义务）

**必须**：在软件中、终端用户可见的位置（建议「关于 / About」页）出现类似文案：

> 本软件界面使用 **MiSans** 字体（© Xiaomi Inc. / 小米科技有限责任公司）。
> MiSans 依据《MiSans 字体知识产权许可协议》免费商用，来源 <https://hyperos.mi.com/font/>。

**建议**（非强制）：安装包内附 `THIRD-PARTY-NOTICES.txt`，写明字体名、版权方、许可名、官方 URL 与本目录 `LICENSE-MiSans.txt` 的许可快照（抓取日期 2026-10-19）。

**禁止**：
- 嵌入字体却不提「MiSans」；
- 把字体改名（如 `MiSans App Edition`）——属第 2 条「改编」；
- 在官网/下载页单开「下载字体」入口，或把 woff2 放在可公开遍历的静态目录；
- 声称字体是「本项目自研字体」。

## 风险与灰区（必须知情）

1. **可撤销授权**：`revocable`，无期限、无通知条款，官方保留随时变更许可的权利。当前结论仅对 2026-10-19 的版本有效。
2. **woff2 / 子集化无明文**：官方从未逐字写「允许格式转换或子集化」。「无损转换 + 字体名与 name 表版权字符串不变 + 字形轮廓不变」是与「不得改编/改外观」最不冲突的做法，但属**解释性结论**，不是逐字授权。若项目法务要求确定性，建议邮件 `misans@xiaomi.com` 取得书面确认并留回执。
3. **中英文本不一致**：中文第 3 条「您不得**单独**将……以及重新分发或售卖」中「单独」统辖全句；英文版断成两句，第二句 `nor may you redistribute or sell the font software or any copies thereof` 缺 `separately`，字面更宽。本文件采用**中文版 + FAQ 明文允许嵌入式字体**的解释（随应用整体分发可行，单独分发字体不可行）。
4. **官方 PDF 未取正文**：<https://hyperos.mi.com/font-download/MiSans字体知识产权许可协议.pdf> 返回 200 / `application/pdf` / 79,535 B / Last-Modified 2023-10-19，但抓取工具不支持 PDF 正文，**PDF 与网页版是否逐字一致未验证**。旁证：字体包内 `LICENSE` 镜像与 npm 转载版与网页版一致（仅把第 3 条拆成两条）。

## 与实现相关的操作约束

- ✅ 从官方 <https://hyperos.mi.com/font/download> 取原始 ttf/otf，自行转 woff2 与子集化；保留 `name` 表版权字符串、PostScript 名 `MiSans-Regular` / 家族名 `MiSans`，不改轮廓与度量。
- ✅ 字体作为应用资源随安装包分发；界面通过本地 `@font-face` 引用，不请求外网。
- ✅ 关于页署名（见上），安装包附 NOTICES + 本目录许可快照。
- ❌ 不提供字体下载入口、不把字体文件单独发布到 CDN/Release 附件里。

## 本次审计相关的实现现状（详见审计报告）

字体的**许可**没问题，但当前仓库的字体**接入**有两个与本文件相关的事实，供实现方注意：

1. `src/public-final/fonts/fonts.css` 里 188 个 `url("./N.woff2")` 指向 `fonts/` 根目录，而分片实际位于 `fonts/misans-regular|medium|demibold/` → **188/188 目标文件缺失**，随包 webfont 实际加载不到；本次渲染能显示 MiSans 是因为本机已安装 `C:\Windows\Fonts\MiSans-Regular.otf`。
2. CSS 只引用了家族 `MiSans`（仅 400 一个字重），`MiSans Medium`（500）与 `MiSans Demibold`（600）两个家族在 `app.css` 中从未被引用 → 500/600 字重全部是 Regular 的**合成粗体**。

审计报告与截图：`evidence/dsh-ui-final/font-audit/REPORT.md`。

## 来源清单（本次实际抓取）

| URL | 结果 |
|---|---|
| <https://hyperos.mi.com/font/download> | 200 ✅ 中文许可全文（本文引用来源 1） |
| <https://hyperos.mi.com/font/faq> | 200 ✅ 中文 FAQ（嵌入字体、免费商用） |
| <https://hyperos.mi.com/font/en/download/> | 200 ✅ 英文许可全文 |
| <https://hyperos.mi.com/font/en/faq/> | 200 ✅ 英文 FAQ |
| <https://hyperos.mi.com/font/about> | 200 ✅ 沿革（2021 发布即宣布免费商用） |
| <https://hyperos.mi.com/font/license> | 404（无独立许可页） |
| <https://hyperos.mi.com/font-download/MiSans字体知识产权许可协议.pdf> | 200 `application/pdf` 79,535 B，正文未提取 |

> 说明：本目录下的许可文本为**合规留证用的逐字存档**，版权归小米科技有限责任公司。产品对外分发时以官方页面当前版本为准。
