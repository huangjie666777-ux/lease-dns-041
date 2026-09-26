# IPv4子网规划服务

C#12与ASP.NET Core8实现的IPv4子网规划服务：在父网段内扣除已占用子网与保留区间后，为各部门分配最小可用二进制地址块，并汇总剩余地址。

## 接口

### GET /healthz
健康检查，返回 {"status":"ok"}。

### POST /plan
请求体（JSON）：

```json
{
  "parentCidr": "10.0.0.0/24",
  "occupiedSubnets": ["10.0.0.0/26"],
  "reservedRanges": [{ "start": "10.0.0.128", "end": "10.0.0.143" }],
  "departments": [{ "id": "eng", "hosts": 50 }]
}
```

- `parentCidr`：父网段，IPv4四段十进制 + 前缀0~32，必须对齐网络边界（主机位必须为零，不静默修正）。
- `occupiedSubnets`：已占用子网CIDR数组，可省略；每个同样须对齐且完整位于父网段内。
- `reservedRanges`：保留地址区间数组，可省略；`start`/`end`均为IPv4地址，包含两端，`start`不得大于`end`，须完整位于父网段内。
- `departments`：部门列表；`id`为非空唯一字符串，`hosts`为正整数。

成功（200）响应：

```json
{
  "departments": [
    { "id": "eng", "cidr": "10.0.0.64/26", "network": "10.0.0.64",
      "broadcast": "10.0.0.127", "firstUsable": "10.0.0.65",
      "lastUsable": "10.0.0.126", "usableCapacity": 62 }
  ],
  "remainingCidrs": ["10.0.0.164/30", "10.0.0.168/29"],
  "summary": { "totalAddresses": 256, "occupiedAddresses": 80,
               "allocatedAddresses": 84, "remainingAddresses": 92 }
}
```

校验失败（400）：`{"errors":[{"field":"departments[1].id","message":"..."}]}`，`field`定位到具体字段。

分配失败（422，不返回部分方案）：`{"failure":{"departmentId":"second","reason":"..."}}`。

## 分配规则

- 占用子网与保留区间按并集（区间合并）从父网段扣除，允许互相重叠。
- 每个部门需要 `hosts+2` 个地址（网络与广播地址不可分配），取能容纳的最小二进制块（前缀 = 32 - ceil(log2(hosts+2))）。
- 处理顺序：所需块大小降序，同大小按部门ID字典序；因此请求中部门顺序不影响结果。
- 每次在剩余空闲区间中选择地址最小且按块大小对齐的连续块，不跨越保留区间，不用零散地址拼凑。
- 剩余地址输出为按地址升序的最少CIDR集合，精确覆盖空闲部分。
- 统计基于区间运算（ulong计数），支持/0大网段，不逐地址枚举。
- 请求相互独立，服务不操作真实网络。

## 示例

```bash
# 保留空洞：保留10.1.0.64-127后，/25只能落在10.1.0.128/25
curl -X POST http://127.0.0.1:5180/plan -H 'Content-Type: application/json' -d '{
  "parentCidr":"10.1.0.0/24",
  "reservedRanges":[{"start":"10.1.0.64","end":"10.1.0.127"}],
  "departments":[{"id":"big","hosts":100}]}

# 容量不足：/25装下100台后，60台的部门无处可去，返回422
curl -X POST http://127.0.0.1:5180/plan -H 'Content-Type: application/json' -d '{
  "parentCidr":"10.2.0.0/25",
  "departments":[{"id":"first","hosts":100},{"id":"second","hosts":60}]}
```

## 开发

```bash
dotnet restore --locked-mode
dotnet test
dotnet build
dotnet run --project src/SubnetPlanner.Api
curl http://127.0.0.1:<启动日志中的端口>/healthz
```

使用.NET SDK8.0.421。NuGet依赖版本保存在锁文件中，项目缓存为本目录.packages。tests/SubnetPlanner.Tests包含xUnit和ASP.NET Core集成测试。

默认仅监听本机，由系统分配空闲端口，实际地址见启动日志；也可通过--urls指定空闲端口。

# DHCP实验

在子网规划服务之上增加了内存版DHCPv4实验服务：客户端通过标准DHCP报文从规划网段获取、续租和归还地址。仅绑定127.0.0.1的可配置高端口（默认6767），不操作网卡、不发送广播。

## 管理接口

### POST /dhcp/activate
请求体为原/plan规划输入，外加三个字段：

- `departmentId`：要启用地址池的部门ID，必须出现在规划结果中。
- `offerSeconds`：报价有效期（正整数秒）。
- `leaseSeconds`：租约时长（正整数秒）。

规划成功后仅启用该部门的可用主机范围（firstUsable~lastUsable），全网同时只启用一个池。存在未过期报价或租约时返回409拒绝替换；非法请求（400/422）不影响旧池。成功响应示例：

```json
{ "activated": true, "departmentId": "eng", "cidr": "10.20.0.64/26",
  "firstUsable": "10.20.0.65", "lastUsable": "10.20.0.126",
  "offerSeconds": 30, "leaseSeconds": 300 }
```

### GET /dhcp/status
返回当前池状态：是否激活、部门、地址范围、未过期报价与租约列表（MAC、地址、到期时间）。

## 协议范围

- 标准DHCPv4二进制报文（RFC2131/RFC2132），UDP收发，回复请求源端点。
- 校验固定头（op/htype/hlen，仅以太网6字节MAC）、magic cookie与选项长度；畸形报文直接忽略，不改变状态、不终止服务。
- 支持DISCOVER、选择阶段REQUEST（含服务器标识）、续租REQUEST（ciaddr）与RELEASE，其余消息类型忽略。
- DISCOVER优先复用该MAC已有地址，否则分配最小空闲地址并限时报价；池耗尽不报价。
- REQUEST选择本服务器且匹配报价时确认租约（ACK），不匹配返回NAK，选择其他服务器不响应；重复确认返回原租约。
- 续租通过ciaddr核对MAC归属后延长租期；RELEASE只能归还自身地址，按协议无响应。
- 响应保留事务ID与客户端MAC，按类型编码服务器标识（54）、地址、子网掩码（1）与租期（51）。
- 租约保存在内存，到期自动回收，重启清空；并发分配不会一址多租。

## 运行与演示

```bash
dotnet restore --locked-mode
dotnet test
dotnet build
dotnet run --project src/SubnetPlanner.Api -- --urls http://127.0.0.1:5180
# DHCP UDP端口由appsettings.json的Dhcp:Port配置（默认6767，0表示系统分配）

# 激活eng部门的地址池
curl --noproxy '*' -X POST http://127.0.0.1:5180/dhcp/activate -H 'Content-Type: application/json' -d '{
  "parentCidr":"10.20.0.0/24",
  "departments":[{"id":"eng","hosts":50}],
  "departmentId":"eng","offerSeconds":30,"leaseSeconds":300}'

# 本机演示客户端：获取 -> 续租 -> 释放
dotnet run --project src/DhcpDemo.Client -- --port 6767 --mac 02:00:00:00:00:01

# 查看池状态
curl --noproxy '*' http://127.0.0.1:5180/dhcp/status
```
