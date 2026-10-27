// Здания (docs/design/visual-style.md §3, §5; VS5): цвет (инстанс MultiMesh или вершина меша чанка) + рёбра 1.25 px + затемнение стен + AO у земли.
// Один шейдер на ближнее кольцо (MultiMesh), LOD2 и импосторы в мешах чанков. Свет обычный (diffuse_lambert, тени принимаются): день/ночь - светом.
// Меши строит BoxBuilder: UV грани (u, v) в [0, 1]; масштаб инстанса на ширину ребра не влияет (fwidth - в экранных пикселях).
// Числа (ширина и гашение рёбер, множители) приходят из Palette.cs.
// X-ray (VS6): у инстансов ближнего кольца высотой >= xray_min_h, чья верхушка ближе к камере, чем min(cam_focus_dist * xray_focus_k, xray_max_dist), здание
// целиком выбрасывается дизерингом (discard по шуму экрана, альфа-смешивания нет - 0 прозрачных поверхностей). Высота = длина столбца Y
// MODEL_MATRIX (единичный бокс MultiMesh); у мешей чанков матрица единичная -> высота 1, x-ray не срабатывает. Тень рисует отдельный
// shadows-only узел без этого шейдера (BuildingLayer), поэтому discard на неё не влияет.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, depth_draw_opaque;

#include "res://Shaders/world.gdshaderinc"

uniform float edge_px = 1.25;
uniform float edge_fade_begin = 1000.0;
uniform float edge_fade_end = 1500.0;
uniform float edge_k = 0.62;      // цвет ребра = заливка * edge_k
uniform float wall_shade = 0.9;   // стены: mix(wall_shade, 1, n_world.y); крыша остаётся цветом палитры
uniform float ao_min = 0.72;
uniform float ao_height = 6.0;    // м от земли, где AO-градиент доходит до 1
uniform float xray_min_h = 15.0;  // м: ниже - x-ray не действует
uniform float xray_max_dist = 200.0; // м: верхний предел дистанции x-ray (дальше камера - не x-ray, как бы далеко ни был фокус)
uniform float xray_focus_k = 1.5; // предел = min(cam_focus_dist * k, max_dist)
uniform float xray_begin = 0.75;   // доля дистанции: ближе - здание исчезло полностью, между begin и 1 - плавный дизеринг

varying float y_world;
varying float n_up;
varying float xr_keep;

void vertex() {
	y_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y;
	n_up = (MODEL_MATRIX * vec4(NORMAL, 0.0)).y;
	float h = length(MODEL_MATRIX[1].xyz);
	float lim = min(cam_focus_dist * xray_focus_k, xray_max_dist);
	float d = distance(CAMERA_POSITION_WORLD, (MODEL_MATRIX * vec4(0.0, 1.0, 0.0, 1.0)).xyz);
	xr_keep = h >= xray_min_h ? smoothstep(xray_begin * lim, lim, d) : 1.0;
}

void fragment() {
	if (xr_keep < 1.0) {
		float noise = fract(52.9829189 * fract(dot(FRAGCOORD.xy, vec2(0.06711056, 0.00583715)))); // interleaved gradient noise
		if (xr_keep <= noise) {
			discard;
		}
	}
	vec3 fill = world_info(COLOR.rgb, 1.0);
	fill *= mix(wall_shade, 1.0, clamp(n_up, 0.0, 1.0));
	fill *= mix(ao_min, 1.0, smoothstep(0.0, ao_height, y_world));
	vec2 e = min(UV, 1.0 - UV) / max(fwidth(UV), vec2(1e-6));
	float edge = 1.0 - smoothstep(edge_px - 0.5, edge_px + 0.5, min(e.x, e.y));
	float fade = 1.0 - smoothstep(edge_fade_begin, edge_fade_end, length(VERTEX));
	ALBEDO = mix(fill, fill * edge_k, edge * fade);
	ROUGHNESS = 0.8;
}
