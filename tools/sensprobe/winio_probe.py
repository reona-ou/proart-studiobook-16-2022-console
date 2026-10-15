# AsusWinIO64.dll 只读 API 逐函数试探（子进程隔离，访问违例只牺牲一个子进程）。
#   python winio_probe.py            父进程：遍历全部候选
#   python winio_probe.py --func N   子进程：只试 N
# 只碰 Read/Get 语义的导出，Set/Write/Clear 一律不调用。
import ctypes as C
from ctypes import c_int, c_void_p, c_char_p
import glob, os, subprocess, sys, time

BUF = 256
SHAPES = [
    ('()', [], c_int),
    ('(int)->int', [c_int], c_int),
    ('(void*)->int', [c_void_p], c_int),
    ('(void*,void*)->int', [c_void_p, c_void_p], c_int),
    ('(int,void*)->int', [c_int, c_void_p], c_int),
    ('(void*,int)->int', [c_void_p, c_int], c_int),
    ('(char*,int)->int', [c_char_p, c_int], c_int),
    ('()->float', [], C.c_float),
    ('(void*)->float', [c_void_p], C.c_float),
]

FUNCS = [
    # 温度
    'Thermal_Read_Cpu_Temperature', 'ProArt_Read_Cpu_Temperature', 'Thermal_Read_UpCpu_Temperature',
    'Thermal_Read_GpuVram_Temperature', 'Thermal_Read_GpuVrm_Temperature',
    'Thermal_Read_GpuTS1L_Temperature', 'Thermal_Read_BoardTS0L_Temperature',
    'Thermal_Read_ACFET_Temperature', 'Thermal_Read_ChargerChoke_Temperature',
    # 功率
    'Thermal_Read_CpuPackagePower', 'Thermal_Read_CpuDptf_PowerLimit1', 'Thermal_Read_CpuDptf_PowerLimit2',
    'Power_Read_ACDCMode', 'Adapter_Read_Standard_Watt', 'Adapter_Read_PDO_Watt',
    'Battery_Read_PackageVoltage', 'Battery_Read_Current', 'Battery_Read_CapacityPercentage',
    'CPU_ReadCpuVoltage', 'CPU_ReadCpuFrequency', 'CPU_ReadCpuMaxFrequency', 'CPU_ReadCPUUsage',
    'CPU_ReadCpuId',
    # 风扇
    'HealthyTable_FanCounts', 'HealthyTable_FanRPM', 'Thermal_Read_Fan', 'ProArt_Read_Fan',
    'WMIGetQuietFan', 'HealthyTable_GetFanTestMode',
    # 版本 / 诊断
    'GetIoDllVersion', 'GetDriverVersion', 'GetRefCount', 'EC_ReadEcFwRev', 'EC_ReadEcChipId',
    # WMI 配置信息（字符串）
    'WMIGetProductName', 'WMIGetModelName', 'WMIGetCPUInfo', 'WMIGetGPUInfo', 'WMIGetMemInfo',
    'WMIGetMemSize', 'WMIGetLCDModel', 'WMIGetSSDModel', 'WMIGetBiosVersion', 'WMIGetOSCaption',
    'WMIGetSystemFamily', 'WMIGetHDDModel', 'GetSENSOR_WMI_Data', 'AppList_HardwareConfig',
]

def dll_path():
    for p in [r'C:\WINDOWS\System32\DriverStore\FileRepository\asussci2.inf_amd64_*\ASUSSystemAnalysis\AsusWinIO64.dll']:
        c = glob.glob(p)
        if c:
            return max(c, key=os.path.getmtime)

def interpret(buf):
    b = bytes(buf)
    i32 = int.from_bytes(b[:4], 'little', signed=True)
    u32 = int.from_bytes(b[:4], 'little')
    i64 = int.from_bytes(b[:8], 'little', signed=True)
    f32 = C.cast(buf, C.POINTER(C.c_float)).contents.value
    f64 = C.cast(buf, C.POINTER(C.c_double)).contents.value
    s = b.split(b'\0')[0]
    txt = s.decode('utf8', 'replace') if all(31 < x or x in (9,) for x in s) and len(s) > 1 else ''
    return f'i32={i32} u32={u32} i64={i64} f32={f32 if abs(f32) < 1e12 else float("nan"):.3f} f64={f64:.4f}' + (f' str={txt!r}' if txt else '')

def child(name):
    path = dll_path()
    dll = C.WinDLL(path)
    if hasattr(dll, 'InitializeWinIo'):
        dll.InitializeWinIo.argtypes = []
        dll.InitializeWinIo.restype = c_int
        print(f'# InitializeWinIo -> {dll.InitializeWinIo()}')
    f = getattr(dll, name, None)
    if f is None:
        print('# 无此导出')
        return 2

    def run(shape_idx, tag_prefix=''):
        tag, at, rt = SHAPES[shape_idx]
        a0 = (C.c_byte * BUF)()
        a1 = (C.c_byte * BUF)()
        C.memset(a0, 0, BUF); C.memset(a1, 0, BUF)
        args = []
        for t in at:
            if t is c_void_p: args.append(a0 if not args else a1)
            elif t is c_char_p: args.append(a0)
            elif t is c_int: args.append(BUF if len(args) == 1 else 0)
        try:
            f.argtypes = at
            f.restype = rt
            r = f(*args)
        except Exception as ex:
            return f'  {tag:20s} 异常 {type(ex).__name__}'
        touched = bytes(a0) != b'\0' * BUF or bytes(a1) != b'\0' * BUF
        val = f'{r:.3f}' if isinstance(r, float) else str(r)
        return f'  {tag:20s} ret={val:14s} out={"[" + interpret(a0) + "]" if touched else "未改动"}'

    print(f'== {name} ==')
    for i in range(len(SHAPES)):
        try:
            print(run(i))
        except Exception as ex:
            print(f'  shape {i} 崩了 {type(ex).__name__}')
    # 单核忙等 3 秒，看同一形状的读数是否随之上升——真值的判据
    import threading
    idx = (0, 1, 4)
    before = [run(i) for i in idx]
    stop = time.time() + 3.0
    def spin():
        x = 0
        while time.time() < stop:
            x = (x + 1) % 1000000007
    t = threading.Thread(target=spin); t.start()
    while time.time() < stop:
        C.windll.kernel32.Sleep(200)
    t.join()
    after = [run(i) for i in idx]
    for i, (b_, a_) in enumerate(zip(before, after)):
        if b_ != a_:
            print(f'  [忙等 3 秒后 SHAPES[{i}] 变化]\n    前 {b_}\n    后 {a_}')
            break
    return 0

def parent():
    print('dll =', dll_path())
    for name in FUNCS:
        try:
            r = subprocess.run([sys.executable, os.path.abspath(__file__), '--func', name],
                               capture_output=True, text=True, timeout=40, encoding='utf8')
            out = (r.stdout or '').strip()
            if out:
                print(out)
            if r.returncode != 0:
                print(f'== {name} == 子进程退出码 {r.returncode} {(r.stderr or "")[:120]}')
        except subprocess.TimeoutExpired:
            print(f'== {name} == 超时')

if __name__ == '__main__':
    sys.exit(child(sys.argv[2]) if len(sys.argv) > 2 and sys.argv[1] == '--func' else parent())
