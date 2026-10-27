// Здания (docs/design/visual-style.md §3, §5; VS5): цвет (инстанс MultiMesh или вершина меша чанка) + рёбра 1.25 px + затемнение стен + AO у земли.
// Один шейдер на ближнее кольцо (MultiMesh), LOD2 и импосторы в мешах чанков. Свет обычный (diffuse_lambert, тени принимаются): день/ночь - светом.
// Меши строит BoxBuilder: UV грани (u, v) в [0, 1]; масштаб инстанса на ширину ребра не влияет (fwidth - в экранных пикселях).
// Числа (ширина и гашение рёбер, множители) приходят из Palette.cs.
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

varying float y_world;
varying float n_up;

void vertex() {
	y_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y;
	n_up = (MODEL_MATRIX * vec4(NORMAL, 0.0)).y;
}

void fragment() {
	vec3 fill = world_info(COLOR.rgb, 1.0);
	fill *= mix(wall_shade, 1.0, clamp(n_up, 0.0, 1.0));
	fill *= mix(ao_min, 1.0, smoothstep(0.0, ao_height, y_world));
	vec2 e = min(UV, 1.0 - UV) / max(fwidth(UV), vec2(1e-6));
	float edge = 1.0 - smoothstep(edge_px - 0.5, edge_px + 0.5, min(e.x, e.y));
	float fade = 1.0 - smoothstep(edge_fade_begin, edge_fade_end, length(VERTEX));
	ALBEDO = mix(fill, fill * edge_k, edge * fade);
	ROUGHNESS = 0.8;
}
