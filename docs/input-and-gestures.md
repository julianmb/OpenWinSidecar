# Input Dispatching & Multi-Touch Gestures

## 1. Overview

Input forwarding in OpenWinSidecar translates client touch gestures, mouse clicks, mouse wheels, and keyboard strokes received over WebSocket or HTTP into native Windows input events using the Win32 `SendInput` API.

---

## 2. Multi-Monitor Coordinate Normalization (`InputDispatcher.cs`)

Windows coordinates for `SendInput` with `MOUSEEVENTF_ABSOLUTE` require mapping pixels to normalized `[0..65535]` space across the **entire virtual desktop bounding rectangle**:

```csharp
// 1. Calculate absolute target pixel on specific monitor
int pixelX = bounds.X + (int)(normalizedX * bounds.Width);
int pixelY = bounds.Y + (int)(normalizedY * bounds.Height);

// 2. Fetch entire virtual desktop bounding box
int vX = GetSystemMetrics(SM_XVIRTUALSCREEN);
int vY = GetSystemMetrics(SM_YVIRTUALSCREEN);
int vW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
int vH = GetSystemMetrics(SM_CYVIRTUALSCREEN);

// 3. Map to 0..65535 normalized absolute space
int normAbsX = (int)(((double)(pixelX - vX) / vW) * 65535.0);
int normAbsY = (int)(((double)(pixelY - vY) / vH) * 65535.0);
```

### Injected Flags:
- `MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK`

---

## 3. Gestures & Client Interaction (`SidecarTcpServer.cs`)

The HTML5 canvas viewer captures raw touch and pointer events and translates them to protocol commands:

### Gesture Mapping Table:
| Gesture | Client Action | Protocol Command | Windows Result |
|---|---|---|---|
| **Single Touch / Drag** | 1 finger tap/drag | `input:down,x,y` / `input:up,x,y` | Left Click / Window Drag |
| **Two-Finger Scroll** | 2 fingers drag up/down | `scroll:<delta>` | Mouse Wheel Scroll (`MOUSEEVENTF_WHEEL`) |
| **Two-Finger Tap** | 2 fingers quick tap (<300ms) | `rightclick` | Context Menu / Right Click |
| **Mouse Wheel** | Desktop browser wheel | `scroll:<delta>` | Mouse Wheel Scroll |
| **Keyboard Press** | Physical / on-screen key | `key:down,<vk>` / `key:up,<vk>` | Native Keypress via `KEYBDINPUT` |
| **Cursor Mode Toggle**| Settings modal change | `cursor:host` / `cursor:touch` / `cursor:client` | Changes host cursor rendering mode |

---

## 4. Keyboard Input Dispatching

In `InputDispatcher.cs`:
```csharp
public void SendKey(ushort virtualKeyCode, bool isDown)
{
    var input = new INPUT
    {
        type = INPUT_KEYBOARD,
        u = new INPUT_UNION
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKeyCode,
                dwFlags = isDown ? 0 : KEYEVENTF_KEYUP
            }
        }
    };
    SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
}
```
Supports standard Windows Virtual Key Codes (`VK_A` through `VK_Z`, arrows, modifiers, enter, backspace, etc.) and Unicode text injection via `SendUnicodeChar` and `SendText`.
