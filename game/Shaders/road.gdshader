// Дороги чанками (docs/design/render.md §3.5, §5.2): цвет полосы из вершин, heatmap = цвет из текстуры SegmentLoads по CUSTOM0.x (сегмент).
// Непрозрачный, без прозрачного прохода. R = SpeedRatio, G = Density/120, B = Count > 0.
// Инфо-режим (visual-style.md §7): цвет тонируется в беж, heatmap слоя «Трафик» накладывается поверх.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, cull_disabled, depth_draw_opaque;

#include "res://Shaders/heat.gdshaderinc"

void vertex() {
	v_idx = CUSTOM0.x;
	v_dist = length((MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz);
}

void fragment() {
	vec3 col = world_info(COLOR.rgb, 1.0);
	float m = heat_mix(v_dist);
	if (m > 0.0 && v_idx > -0.5) {
		vec4 h = heat_fetch(v_idx);
		if (h.b > 0.5) {
			float t = max(1.0 - h.r, min(h.g * 0.6, 1.0));
			col = mix(col, heat_ramp(t), m);
		}
	}
	ALBEDO = col;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
	NORMAL = normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz); // плоские ленты: нормаль вверх независимо от обхода граней
}
