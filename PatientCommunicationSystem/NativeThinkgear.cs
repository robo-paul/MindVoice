using System.Runtime.InteropServices;

namespace libStreamSDK
{
    public static class NativeThinkgear
    {
        // Baud rate constants
        public const int TG_BAUD_1200 = 1200;
        public const int TG_BAUD_2400 = 2400;
        public const int TG_BAUD_4800 = 4800;
        public const int TG_BAUD_9600 = 9600;
        public const int TG_BAUD_57600 = 57600;
        public const int TG_BAUD_115200 = 115200;

        // Data format constants
        public const int TG_STREAM_PACKETS = 0;
        public const int TG_STREAM_5VRAW = 1;
        public const int TG_STREAM_RAW = 2;

        // Data type constants
        public const int TG_DATA_POOR_SIGNAL = 0;
        public const int TG_DATA_ATTENTION = 1;
        public const int TG_DATA_MEDITATION = 2;
        public const int TG_DATA_RAW = 3;
        public const int TG_DATA_DELTA = 4;
        public const int TG_DATA_THETA = 5;
        public const int TG_DATA_ALPHA1 = 6;
        public const int TG_DATA_ALPHA2 = 7;
        public const int TG_DATA_BETA1 = 8;
        public const int TG_DATA_BETA2 = 9;
        public const int TG_DATA_GAMMA1 = 10;
        public const int TG_DATA_GAMMA2 = 11;
        public const int TG_DATA_BLINK_STRENGTH = 22;

        // DLL imports
        [DllImport("thinkgear64.dll")]
        public static extern int TG_GetNewConnectionId();

        [DllImport("thinkgear64.dll")]
        public static extern int TG_Connect(int connectionId, string port, int baudrate, int dataFormat);

        [DllImport("thinkgear64.dll")]
        public static extern int TG_ReadPackets(int connectionId, int packets);

        [DllImport("thinkgear64.dll")]
        public static extern int TG_GetValueStatus(int connectionId, int dataType);

        [DllImport("thinkgear64.dll")]
        public static extern float TG_GetValue(int connectionId, int dataType);

        [DllImport("thinkgear64.dll")]
        public static extern void TG_Disconnect(int connectionId);

        [DllImport("thinkgear64.dll")]
        public static extern void TG_FreeConnection(int connectionId);

        [DllImport("thinkgear64.dll")]
        public static extern int TG_GetEngineVersion();
    }
}