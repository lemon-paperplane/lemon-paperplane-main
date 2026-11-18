# Lemon Paperplane — Linux

下一轮实现。界面技术待定（GTK4 + libadwaita 为首选），要求与 macOS / Windows 版外观一致，照 `../docs/design-system.md`、`../shared/design-tokens.json` 实现；SVG 图标 GTK 原生支持。内核与插件用 `GOOS=linux` 交叉编译即可，数据目录为 `$XDG_CONFIG_HOME/lemon-paperplane/`。
