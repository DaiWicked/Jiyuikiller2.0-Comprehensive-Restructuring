// ============================================================================
//  NavBarLens.hlsl —— 底栏"液态玻璃"折射着色器
//
//  编译命令（Windows SDK 自带 fxc）：
//    fxc /T ps_2_0 /E main /Fo NavBarLens.ps NavBarLens.hlsl
//
//  设计要点（对应"液态玻璃"的 5 个光学成分里的第 2、3、4 项）：
//    1. 透明+模糊  -> 由外层 WPF 的 BlurEffect 负责，本 shader 不重复做（ps_2_0 只有
//                     32 条纹理指令，写不下高斯核；分层做质量更好也更便宜）
//    2. 折射      -> 沿半径方向"向内"位移 = 凸透镜 = 内容被放大（这就是"水滴缩放"）
//    3. 色散      -> R/G/B 按 (1+a) / 1 / (1-a) 三种缩放采样同一个位移向量，
//                     产生边缘彩边；因为位移本身已按"边缘因子"加权，中心色散自动为 0
//    4. 边缘柔光  -> 比折射带更窄的一圈菲涅尔式加光，做出玻璃"厚度感"
//
//  ★ 防止"整片偏色"的关键：折射量、色散量、柔光量三者全部乘以 edge 因子，
//    而 edge 在玻璃中心严格为 0；中心区域本 shader 是恒等变换（像素原样透出）。
// ============================================================================

sampler2D InputSampler : register(s0);

float2 TextureSize     : register(c0);  // 元素尺寸（设备像素）
float2 GlassHalf       : register(c1);  // 玻璃圆角矩形的半尺寸（设备像素）
float  GlassRadius     : register(c2);  // 圆角半径（设备像素）
float  EdgeWidth       : register(c3);  // 边缘折射带宽度（设备像素）
float  RefractStrength : register(c4);  // 最大向内位移量（设备像素）
float  AberrationPx    : register(c5);  // 色散的绝对像素量（R/B 各偏 ±此值）
float  RimBoost        : register(c6);  // 边缘柔光强度
float  Strength        : register(c7);  // 总强度倍率（0 = 完全关闭）

// 圆角矩形有符号距离场：内部为负、外部为正
float sdRoundBox(float2 p, float2 halfExtent, float radius)
{
    float2 q = abs(p) - halfExtent + radius;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - radius;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 px = uv * TextureSize;
    float2 p  = px - TextureSize * 0.5;                 // 以元素中心为原点

    // 距玻璃边缘的"深度"：边缘处 0，越往里越大
    float depth = -sdRoundBox(p, GlassHalf, max(GlassRadius, 0.5));

    // 边缘因子：只有离边缘 EdgeWidth 以内的像素非零，中心严格为 0
    float edge = saturate(1.0 - depth / max(EdgeWidth, 1.0));
    edge = edge * edge * Strength;

    // 中心 -> 当前像素的方向
    float2 dir = normalize(p + float2(1e-5, 1e-5));

    // 折射：沿半径向内位移 => 视觉上把背景内容"拉进来"，即透镜放大（水滴感）
    float2 offsetUV = (-dir * edge * RefractStrength) / max(TextureSize, float2(1.0, 1.0));

    // 色散：R 少偏折、B 多偏折（真实玻璃的色散方向），用"绝对像素量"而不是比例 ——
    // 比例写法(如 ±2%)在 5.6px 位移下只有 0.14px 差，肉眼完全看不到、也无法测量。
    // 这一项同样乘 edge，所以中心色散严格为 0。
    float2 abUV = (dir * (edge * AberrationPx)) / max(TextureSize, float2(1.0, 1.0));

    float3 c;
    c.r = tex2D(InputSampler, uv + offsetUV + abUV).r;
    c.g = tex2D(InputSampler, uv + offsetUV).g;
    c.b = tex2D(InputSampler, uv + offsetUV - abUV).b;

    // 边缘柔光（菲涅尔）：带宽只有折射带的 35%，冷色偏移，做出玻璃厚度感
    float rim = saturate(1.0 - depth / max(EdgeWidth * 0.35, 1.0));
    rim = rim * rim * rim * RimBoost * Strength;
    c += rim * float3(0.92, 0.96, 1.05);

    return float4(saturate(c), 1.0);
}
