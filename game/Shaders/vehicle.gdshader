// ТС LOD0/LOD1 (docs/design/visual-style.md §3, §5): цвет инстанса + рёбра + затемнение стен + AO-градиент у земли.
// Свет обычный (diffuse_lambert, тени принимаются): день/ночь - освещением, поэтому light_tint здесь не нужен.
// Меш строит BoxBuilder: UV грани (u, v) в [0, 1], v = 0 снизу; низ меша на y = 0 (инстанс стоит на земле), поэтому y_local = VERTEX.y.
// Цвет инстанса: rgb - цвет, a - доля тонирования инфо-режима (0 у stuck/выделенных: насыщенный оверлей поверх беж).
// Числа (ширина и гашение рёбер, множители) приходят из Palette.cs.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, depth_draw_opaque;

#include "res://Shaders/world.gdshaderinc"

uniform float edge_px = 1.0;
uniform float edge_fade_begin = 120.0;
uniform float edge_fade_end = 200.0;
uniform float edge_k = 0.5;      // цвет ребра = заливка * edge_k
uniform float wall_shade = 0.9;  // стены: mix(wall_shade, 1, n_world.y); крыша остаётся цветом палитры
uniform float ao_min = 0.8;
uniform float ao_height = 0.8;   // м от земли, где AO-градиент доходит до 1

varying float y_local;
varying float n_up;

void vertex() {
	y_local = VERTEX.y;
	n_up = (MODEL_MATRIX * vec4(NORMAL, 0.0)).y;
}

void fragment() {
	vec3 fill = world_info(COLOR.rgb, COLOR.a);
	fill *= mix(wall_shade, 1.0, clamp(n_up, 0.0, 1.0));
	fill *= mix(ao_min, 1.0, smoothstep(0.0, ao_height, y_local));
	vec2 e = min(UV, 1.0 - UV) / max(fwidth(UV), vec2(1e-6));
	float edge = 1.0 - smoothstep(edge_px - 0.5, edge_px + 0.5, min(e.x, e.y));
	float fade = 1.0 - smoothstep(edge_fade_begin, edge_fade_end, length(VERTEX));
	ALBEDO = mix(fill, fill * edge_k, edge * fade);
	ROUGHNESS = 0.7;
}
