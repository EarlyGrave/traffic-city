// Дороги чанками (docs/design/render.md §3.5, §5.2): цвет асфальта из вершин, heatmap = цвет из текстуры SegmentLoads по CUSTOM0.x (сегмент).
// Непрозрачный, без прозрачного прохода. R = SpeedRatio, G = Density/120, B = Count > 0.
// Инфо-режим (visual-style.md §7): цвет тонируется в беж, heatmap слоя «Трафик» накладывается поверх.
//
// Линии полос (VS3a), только ближний LOD, толщина постоянная в пикселях, гаснут с дистанцией; прозрачность - смешением цвета с асфальтом:
//   CUSTOM0.y = 0  - голый асфальт (LOD1, дальняя лента, площадки перекрёстков);
//   CUSTOM0.y >= 2 - лента сегмента: nTot + 16 * nBase (nTot полос, nBase - полос направления, от правого края которого отсчитан UV.y; 0 - без осевой);
//                    UV.y = поперечная координата в полосах от 0 до nTot, разделители - целые значения 1..nTot-1;
//   CUSTOM0.y = -1 - направляющая коннектора: UV.x = -1..1 поперёк, CUSTOM0.zw = смещение кромки (нормаль * сторона), расширяется в вершинном шейдере до line_px.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, cull_disabled, depth_draw_opaque;

#include "res://Shaders/heat.gdshaderinc"

uniform vec3 line_color : source_color = vec3(0.3, 0.3, 0.3);
uniform float line_alpha = 0.2;          // доля line_color в линии разделителя
uniform float line_center_alpha = 0.36;  // то же, для границы направлений
uniform float line_px = 1.2;             // толщина линии, пикселей экрана
uniform float line_fade_begin = 90.0;    // линии гаснут между этими дистанциями, м (от LodConfig.RoadLod0)
uniform float line_fade_end = 175.0;

varying flat float v_kind;
varying vec2 v_uv;

void vertex() {
	v_idx = CUSTOM0.x;
	v_kind = CUSTOM0.y;
	v_uv = UV;
	vec3 vp = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	if (CUSTOM0.y < -0.5) {
		// метров на пиксель на этой глубине: кромка линии сдвигается на полтолщины (+ запас под сглаживание)
		float mpp = 2.0 * max(-vp.z, 0.01) / (PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y);
		VERTEX.xz += CUSTOM0.zw * (0.5 * line_px + 0.5) * mpp;
		vp = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	}
	v_dist = length(vp);
}

void fragment() {
	vec3 base = COLOR.rgb; // асфальт (Palette.Asphalt) на всех LOD; Closing - тон поверх
	float k = 0.0;
	int code = int(v_kind + 0.5);
	float fade = 1.0 - smoothstep(line_fade_begin, line_fade_end, v_dist);
	if (v_kind > 1.5 && fade > 0.0) {
		int nt = code & 15;
		int nb = code >> 4;
		float v = v_uv.y;
		float px = max(fwidth(v), 1e-5);              // полос на пиксель
		float ln = clamp(round(v), 1.0, float(nt - 1));
		float d_px = abs(v - ln) / px;                // расстояние до ближайшего разделителя, пикселей
		float cov = clamp(0.5 * line_px + 0.5 - d_px, 0.0, 1.0);
		cov *= 1.0 - smoothstep(0.25, 0.5, px);       // полоса уже ~2-4 px: линии превращаются в муар - гасим
		float a = (nb > 0 && int(ln + 0.5) == nb) ? line_center_alpha : line_alpha;
		k = cov * a * fade;
	} else if (v_kind < -0.5 && fade > 0.0) {
		float d_px = abs(v_uv.x) * (0.5 * line_px + 0.5);
		k = clamp(0.5 * line_px + 0.5 - d_px, 0.0, 1.0) * line_alpha * fade;
	}
	vec3 col = world_info(mix(base, line_color, k), 1.0);
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
