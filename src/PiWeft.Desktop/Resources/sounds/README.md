# 提示音预设

这 7 个 WAV 与 [PiDeck](https://github.com/pideck) 的提示音**逐字节相同**，由
`scripts/gen-sound-presets.mjs` 用纯正弦 + 谐波合成（16-bit PCM、单声道 44.1 kHz、
每个 0.3–0.9 s），不含任何采样素材，因此没有第三方音频许可问题。

| 文件 | 用途 |
|---|---|
| `done-chime.wav` | 会话完成（默认） |
| `done-bell.wav` | 会话完成（备选） |
| `done-pop.wav` | 会话完成（备选） |
| `error-buzz.wav` | 会话异常（默认） |
| `error-alert.wav` | 会话异常（备选） |
| `waiting-ping.wav` | 等待用户输入（默认） |
| `waiting-knock.wav` | 等待用户输入（备选） |

需要重新生成时在 PiDeck 仓库执行 `node scripts/gen-sound-presets.mjs`，再把
`sounds/` 下的 wav 复制过来；PiWeft 侧只按文件名引用，不改音色。

播放路径：`SoundPlayer` 把嵌入资源写到 `<agentDir>/sounds/` 缓存，交给平台播放器
（Windows `winmm!PlaySound`、macOS `afplay`、Linux `paplay`/`pw-play`/`aplay`）。
音量在缓存副本上按 16-bit 样本缩放，播放器本身不需要音量参数。
