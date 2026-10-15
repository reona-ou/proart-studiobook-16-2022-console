import os, re, sys, uuid, shutil, hashlib

# 待打包的发布产物目录：默认取脚本同级的 payload\，也可用第一个参数指定
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.join(HERE, 'payload')
KEEP = {'Assets', 'Microsoft.UI.Xaml', 'zh-CN', 'en-us', 'en-US', 'zh-Hans'}
LOCALE = re.compile(r'^[A-Za-z]{2}(?:-[A-Za-z]{2,10})?(?:-[A-Za-z]{2,10})?$')

# 1) 裁语言目录（只动这份发布副本）
removed = []
for name in sorted(os.listdir(ROOT)):
    p = os.path.join(ROOT, name)
    if os.path.isdir(p) and name not in KEEP and LOCALE.match(name) and name not in ('Assets',):
        shutil.rmtree(p, ignore_errors=True)
        removed.append(name)

files = []
for dirpath, _, fnames in os.walk(ROOT):
    for fn in fnames:
        full = os.path.join(dirpath, fn)
        rel = os.path.relpath(full, ROOT).replace('\\', '/')
        files.append((rel, full))

# 2) 目录树
dirs = sorted({os.path.dirname(rel) for rel, _ in files if '/' in rel}, key=lambda d: (d.count('/'), d))
# 补齐中间层目录（有些层没有直接文件，但必须作为父目录声明出来）
all_dirs = set(dirs)
for d in list(dirs):
    p = d
    while '/' in p:
        p = p.rsplit('/', 1)[0]
        all_dirs.add(p)
dirs = sorted(all_dirs, key=lambda d: (d.count('/'), d))

def did(path):
    return 'D_' + re.sub(r'[^A-Za-z0-9]', '_', path).upper()[:40] + hashlib.sha1(path.encode()).hexdigest()[:6].upper()

def fid(rel):
    return 'F_' + re.sub(r'[^A-Za-z0-9_]', '_', os.path.basename(rel)).upper()[:36] + hashlib.sha1(rel.encode()).hexdigest()[:8].upper()

UP = '{' + str(uuid.uuid5(uuid.NAMESPACE_URL, 'proart-console-wix3-upgrade')).upper() + '}'

def comp(rel):
    g = '{' + str(uuid.uuid5(uuid.NAMESPACE_URL, 'proart-console-f:' + rel)).upper() + '}'
    target = did(os.path.dirname(rel)) if '/' in rel else 'APPLICATIONFOLDER'
    idv = fid(rel)
    # 每用户安装（LocalAppData）时 ICE38 要求 KeyPath 是 HKCU 注册表项，不能是文件
    return ('      <Component Id="%s" Guid="%s">\n'
            '        <RegistryValue Root="HKCU" Key="Software\\ProArtConsole\\Files" Name="%s" Type="integer" Value="1" KeyPath="yes" />\n'
            '        <File Id="%s" Source="payload\\%s" />\n'
            '      </Component>\n') % (idv, g, idv, idv, rel.replace('/', '\\')), target

by_dir = {}
for rel, _ in files:
    _, target = comp(rel)
    by_dir.setdefault(target, []).append(rel)

x = []
x.append('<?xml version="1.0" encoding="utf-8"?>\n<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">\n')
x.append('  <Product Id="*" Name="ProArt Studiobook 16 性能控制台" Language="2052" Codepage="936"\n')
x.append('           Version="1.0.0" Manufacturer="local" UpgradeCode="%s">\n' % UP)
x.append('    <Package InstallerVersion="500" Compressed="yes" InstallScope="perUser" Platform="x64"\n')
x.append('             Description="ProArt Studiobook 16 性能控制台" Comments="风扇档位与温度监控控制台" />\n')
x.append('    <MajorUpgrade AllowSameVersionUpgrades="yes" DowngradeErrorMessage="请先卸载已安装的版本。" />\n')
x.append('    <MediaTemplate EmbedCab="yes" CompressionLevel="high" />\n')
x.append('    <Icon Id="appicon" SourceFile="payload\\Assets\\tray.ico" />\n')
x.append('    <Property Id="ARPPRODUCTICON" Value="appicon" />\n')

# 目录层级：逐个嵌套展开
x.append('    <Directory Id="TARGETDIR" Name="SourceDir">\n')
x.append('      <Directory Id="LocalAppDataFolder">\n')
x.append('        <Directory Id="APPLICATIONFOLDER" Name="ProArtConsole">\n')
tree = {}
for d in dirs:
    tree[d] = None
for d in dirs:
    parent = os.path.dirname(d).replace('\\', '/')
    tree[d] = ('APPLICATIONFOLDER' if parent == '' else did(parent))
# 按父子顺序输出
emitted = set()
def emit_dir(d, indent):
    name = os.path.basename(d)
    x.append('%s<Directory Id="%s" Name="%s">\n' % (' ' * indent, did(d), name))
    emitted.add(d)
    for c in dirs:
        if os.path.dirname(c).replace('\\', '/') == d and c not in emitted:
            emit_dir(c, indent + 2)
    x.append('%s</Directory>\n' % (' ' * indent))
for d in dirs:
    if os.path.dirname(d).replace('\\', '/') == '' and d not in emitted:
        emit_dir(d, 10)
x.append('        </Directory>\n      </Directory>\n')
x.append('      <Directory Id="ProgramMenuFolder">\n')
x.append('        <Directory Id="APPMENUFOLDER" Name="ProArt 性能控制台" />\n      </Directory>\n')
x.append('    </Directory>\n')

x.append('    <DirectoryRef Id="APPMENUFOLDER">\n')
x.append('      <Component Id="CMP_SHORTCUT" Guid="%s">\n' % ('{' + str(uuid.uuid5(uuid.NAMESPACE_URL, 'proart-console-sc')).upper() + '}'))
x.append('        <Shortcut Id="StartMenu" Name="ProArt 性能控制台" Target="[APPLICATIONFOLDER]ProArtConsole.exe"\n')
x.append('                 WorkingDirectory="APPLICATIONFOLDER" Icon="appicon" Advertise="no" />\n')
x.append('        <RemoveFolder Id="RemoveMenu" On="uninstall" />\n')
x.append('        <RegistryValue Root="HKCU" Key="Software\\ProArtConsole" Name="shortcut" Type="integer" Value="1" KeyPath="yes" />\n')
x.append('      </Component>\n    </DirectoryRef>\n')

# 每用户安装落在 LocalAppData 里，ICE64 要求用户配置文件中的目录在 RemoveFile 表中有记录，
# 否则卸载后目录会残留。为每个目录补一条 RemoveFolder。
def cleanup(d):
    g = '{' + str(uuid.uuid5(uuid.NAMESPACE_URL, 'proart-console-cln:' + d)).upper() + '}'
    idv = 'CLN_' + re.sub(r'[^A-Za-z0-9]', '_', d).upper()[:40]
    return ('      <Component Id="%s" Guid="%s" Directory="%s">\n'
            '        <RegistryValue Root="HKCU" Key="Software\\ProArtConsole\\Files" Name="%s" Type="integer" Value="1" KeyPath="yes" />\n'
            '        <RemoveFolder Id="%s" Directory="%s" On="uninstall" />\n'
            '      </Component>\n') % (idv, g, d, idv, 'RF_' + idv, d)

groups = sorted(by_dir)
x.append('    <Feature Id="Main" Title="ProArt 性能控制台" Level="1" Display="collapse" Absent="disallow" AllowAdvertise="no">\n')
for g in groups:
    x.append('      <ComponentGroupRef Id="GRP_%s" />\n' % g.replace('APPLICATIONFOLDER', 'ROOT'))
x.append('      <ComponentGroupRef Id="GRP_CLEANUP" />\n')
x.append('      <ComponentRef Id="CMP_SHORTCUT" />\n    </Feature>\n  </Product>\n')

x.append('  <Fragment>\n    <ComponentGroup Id="GRP_CLEANUP">\n')
for d in ['APPLICATIONFOLDER'] + [did(p) for p in dirs]:
    x.append(cleanup(d))
x.append('    </ComponentGroup>\n  </Fragment>\n')

for g in groups:
    gid = 'GRP_' + g.replace('APPLICATIONFOLDER', 'ROOT')
    x.append('  <Fragment>\n    <ComponentGroup Id="%s" Directory="%s">\n' % (gid, g))
    for rel in sorted(by_dir[g]):
        c, _ = comp(rel)
        x.append(c)
    x.append('    </ComponentGroup>\n  </Fragment>\n')
x.append('</Wix>\n')

open(os.path.join(HERE, 'product.wxs'), 'w', encoding='utf-8').write(''.join(x))
print('removed=%s files=%d groups=%d dirs=%s' % (removed, len(files), len(groups), dirs))
