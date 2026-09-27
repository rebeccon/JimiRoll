# 工坊发布记录（JimiRoll）

> 本文件是**工坊物品 ID 的权威记录**。`mod_id.txt` 是上传器写在本地工作区里的，
> 丢了就会变成"新建物品"而不是"更新"。这里再记一份，双保险。

| 项 | 值 |
|---|---|
| **工坊物品 ID** | `3808993605` |
| 工坊链接 | https://steamcommunity.com/sharedfiles/filedetails/?id=3808993605 |
| 发布账号 | 三氧水 |
| 首次上传 | 2026-09-27（mod 版本 2.2.0） |
| 上传器 | [megacrit/sts2-mod-uploader](https://github.com/megacrit/sts2-mod-uploader) v0.2.0 |
| 本地工作区 | `C:\Users\rebeccon\Downloads\ModUploader-win-x64\JimiRoll` |
| 标签 | `QoL`、`Utility`（★ 上传后只能在网页改 UI，改值要重新上传） |

## 上传 / 更新命令

在 `ModUploader.exe` 所在目录：

```cmd
ModUploader.exe upload -w JimiRoll
:: 或
upload.cmd JimiRoll
```

## 更新流程

1. `build.ps1` 重新打包
2. 把新的模组文件放进工作区的 `content\JimiRoll\`（旧的先删干净）
3. 把 `workshop.update.json` 复制成 `workshop.json` 并改好 `changeNote`
   —— **只留 changeNote**，其余字段省略 = 不动工坊上已有的简介/依赖/标签
4. 跑上面那条上传命令（物品 ID 自动从 `mod_id.txt` 读）
5. 上传完在本文件下面追加一行版本记录

## 版本记录

| mod 版本 | 上传日期 | changeNote |
|---|---|---|
| 2.2.0 | 2026-09-27 | 首发版本：筛选指标为「第一章出怪房间占比最低」 |
