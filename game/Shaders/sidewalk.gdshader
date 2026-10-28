// Тротуары и переходы чанками (docs/design/render.md §3.5): ленты непрозрачные, ширина = ширина тротуара; heatmap из PedEdgeCounts по CUSTOM0.x (ребро).
// CUSTOM0.y = 1 у перехода: он нужен только как heatmap-лента (вблизи зебра рисуется отдельно, лента отбрасывается).
// R = плотность (нормирована в коде), B = на ребре кто-то есть.
// Инфо-режим (visual-style.md §7): цвет тонируется в беж, heatmap слоя «Пешеходы» накладывается поверх.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, cull_disabled, depth_draw_opaque;

#include "res://Shaders/heat.gdshaderinc"

varying flat float v_cross;

void vertex() {
	v_idx = CUSTOM0.x;
	v_cross = CUSTOM0.y;
	v_dist = length((MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz);
}

void fragment() {
	vec3 col = world_info(COLOR.rgb, 1.0);
	float m = heat_mix(v_dist);
	if (v_cross > 0.5 && m <= 0.001) {
		discard;
	}
	if (m > 0.0) {
		vec4 h = heat_fetch(v_idx);
		if (h.b > 0.5) {
			col = mix(col, heat_ramp(h.r), m);
		}
	}
	ALBEDO = col;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
	NORMAL = normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz); // плоские ленты: нормаль вверх независимо от обхода граней
}
