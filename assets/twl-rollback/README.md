# TWL 启动器回滚资产（2026-11-21 闪事故沉淀）

- 事故：11-21 TWL 启动器自动更新后，游戏进主菜单数分钟内无声原生闪退（C# 零异常、boot 闸门全过、crashCount 不涨）
- 定罪：新 TWL 内 NeoModLoader.dll 与旧版逐字节一致（md5 5ae2bf3210c3d064ac0fb59851c0c4a2）→ 变的是 TWL 壳层，NML/mod/游戏本体三方清白
- 处置：回滚 TWL-1.0.66
- 资产：
  - TWL-1.0.66-release.zip（旧版启动器安装包，内有 APK）
  - NeoModLoader_1.2.1.1.dll（现行 NML，比对基准）
- 教训：TWL 是全管线单点故障源，禁自动更新，更新后必做 DLL md5 比对再放 mod 进场
