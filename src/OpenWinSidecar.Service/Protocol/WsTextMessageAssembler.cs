using System.Text;

namespace OpenWinSidecar.Service.Protocol;

/// <summary>
/// A buffering WebSocket text-message parser. Browsers legitimately coalesce several
/// WebSocket frames into one TCP segment (Safari does this on the connect burst), and
/// reads may also split a frame across segments — the old one-parse-per-read loop
/// dropped every coalesced message after the first, losing 'codec:hevc' and friends.
/// Feed every read; complete text messages come out in order.
///
/// Also reassembles fragmented messages (FIN=0 initial frame + continuation frames, RFC 6455
/// §5.4) and skips interleaved control frames (close/ping/pong) without disturbing reassembly.
/// </summary>
internal sealed class WsTextMessageAssembler
{
    private readonly byte[] _acc = new byte[64 * 1024];
    private int _accLen;

    private readonly List<byte> _fragment = new();
    private bool _inFragment;
    private bool _fragmentIsText;

    public void OnData(byte[] data, int length, Action<string> onMessage)
    {
        if (_accLen + length > _acc.Length)
        {
            _accLen = 0; // malformed overflow — reset
            _inFragment = false;
            _fragment.Clear();
        }

        Buffer.BlockCopy(data, 0, _acc, _accLen, length);
        _accLen += length;

        // Decode as many complete frames as are buffered
        int pos = 0;
        while (true)
        {
            if (_accLen - pos < 2) break;

            bool masked = (_acc[pos + 1] & 0x80) != 0;
            int payloadLen = _acc[pos + 1] & 0x7F;
            int headerLen = 2;

            if (payloadLen == 126)
            {
                if (_accLen - pos < 4) break;
                payloadLen = (_acc[pos + 2] << 8) | _acc[pos + 3];
                headerLen = 4;
            }
            else if (payloadLen == 127)
            {
                if (_accLen - pos < 10) break;
                payloadLen = (int)((long)_acc[pos + 2] << 56 | (long)_acc[pos + 3] << 48 |
                                   (long)_acc[pos + 4] << 40 | (long)_acc[pos + 5] << 32 |
                                   (long)_acc[pos + 6] << 24 | (long)_acc[pos + 7] << 16 |
                                   (long)_acc[pos + 8] << 8  | _acc[pos + 9]);
                headerLen = 10;
            }

            int maskLen = masked ? 4 : 0;
            int frameEnd = pos + headerLen + maskLen + payloadLen;
            if (frameEnd > _accLen) break; // partial frame — wait for more data

            int opcode = _acc[pos] & 0x0F;
            bool isFinal = (_acc[pos] & 0x80) != 0;

            if (opcode >= 0x8)
            {
                // Control frames: consume and ignore, never disturb an in-progress message.
                pos = frameEnd;
                continue;
            }

            if (opcode == 0x0)
            {
                // Continuation: append only inside a fragmented message.
                if (_inFragment)
                {
                    AppendPayload(pos, headerLen, maskLen, payloadLen);
                    if (isFinal)
                    {
                        if (_fragmentIsText && _fragment.Count > 0)
                            onMessage(Encoding.UTF8.GetString(_fragment.ToArray()));
                        _inFragment = false;
                        _fragment.Clear();
                    }
                }
                pos = frameEnd;
                continue;
            }

            if (opcode == 0x1 || opcode == 0x2)
            {
                // A new data frame aborts any unfinished fragment (protocol error; be liberal).
                _inFragment = false;
                _fragment.Clear();

                if (isFinal)
                {
                    if (opcode == 0x1 && payloadLen > 0)
                        onMessage(DecodePayload(pos, headerLen, maskLen, payloadLen));
                }
                else
                {
                    _inFragment = true;
                    _fragmentIsText = opcode == 0x1;
                    AppendPayload(pos, headerLen, maskLen, payloadLen);
                }
                pos = frameEnd;
                continue;
            }

            // Unknown opcode: consume to stay in sync.
            pos = frameEnd;
        }

        // Keep any trailing partial frame for the next read
        if (pos > 0)
        {
            Buffer.BlockCopy(_acc, pos, _acc, 0, _accLen - pos);
            _accLen -= pos;
        }
    }

    private void AppendPayload(int pos, int headerLen, int maskLen, int payloadLen)
    {
        int dataStart = pos + headerLen + maskLen;
        for (int i = 0; i < payloadLen; i++)
        {
            byte b = _acc[dataStart + i];
            if (maskLen > 0)
                b ^= _acc[pos + headerLen + (i & 3)];
            _fragment.Add(b);
        }
    }

    private string DecodePayload(int pos, int headerLen, int maskLen, int payloadLen)
    {
        var decoded = new byte[payloadLen];
        int dataStart = pos + headerLen + maskLen;
        if (maskLen > 0)
        {
            for (int i = 0; i < payloadLen; i++)
                decoded[i] = (byte)(_acc[dataStart + i] ^ _acc[pos + headerLen + (i & 3)]);
        }
        else
        {
            Buffer.BlockCopy(_acc, dataStart, decoded, 0, payloadLen);
        }
        return Encoding.UTF8.GetString(decoded);
    }
}
