// Общее для heatmap в шейдере дорог и тротуаров (docs/design/render.md §3.5, visual-style.md §7).
// Данные - текстура RGBA8 шириной 4096 (HeatTextures.cs), индекс из CUSTOM0.x; меш при смене нагрузки не трогаем.
// Палитра (c_low/c_mid/c_high) задаётся одним местом в коде - Palette.cs через HeatPalette.cs; здесь значения по умолчанию не значимы.
// Heatmap виден только в инфо-режиме (world.gdshaderinc): вес = info_mode, если info_layer == heat_layer материала.
#include "res://Shaders/world.gdshaderinc"

uniform sampler2D heat_tex : filter_nearest, repeat_disable;
uniform float heat_force = 0.0;   // 1 - heatmap везде (отладка: --ped-mode heat), -1 - никогда (--ped-mode agents), 0 - по инфо-режиму
uniform float heat_auto = 0.0;    // 1 - авто-heatmap дальше heat_dist в основном виде (выключен решением владельца, HeatPalette.AutoFar)
uniform int heat_layer = 1;       // слой инфо-режима, в котором материал показывает heatmap
uniform float heat_dist = 2500.0; // P: дальше дистанции точек - heatmap (smoothstep(0.9P, P, d)), если heat_auto > 0.5
uniform vec3 c_low : source_color = vec3(0.0, 1.0, 0.0);
uniform vec3 c_mid : source_color = vec3(1.0, 1.0, 0.0);
uniform vec3 c_high : source_color = vec3(1.0, 0.0, 0.0);

varying flat float v_idx;
varying float v_dist;

vec3 heat_ramp(float t) {
	t = clamp(t, 0.0, 1.0);
	return t < 0.5 ? mix(c_low, c_mid, t * 2.0) : mix(c_mid, c_high, (t - 0.5) * 2.0);
}

vec4 heat_fetch(float idx) {
	int i = int(idx + 0.5);
	return texelFetch(heat_tex, ivec2(i & 4095, i >> 12), 0);
}

float heat_mix(float d) {
	if (heat_force > 0.5) {
		return 1.0;
	}
	if (heat_force < -0.5) {
		return 0.0;
	}
	float m = info_layer == heat_layer ? info_mode : 0.0;
	if (heat_auto > 0.5) {
		m = max(m, smoothstep(0.9 * heat_dist, heat_dist, d));
	}
	return m;
}
