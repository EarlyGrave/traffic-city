// Слой земли (VS9, docs/design/visual-style.md §8): парки, пруды, вода и тон кварталов одним мешем на дальний чанк.
// COLOR - тон основного вида (Palette.cs, линейный), CUSTOM0.rgb - насыщенный цвет инфо-слоя «Зоны» (3), UV.x - близость к кромке (0..1) для светлой полосы берега.
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, depth_draw_opaque, shadows_disabled;

#include "res://Shaders/world.gdshaderinc"

uniform vec3 shore_color : source_color = vec3(0.92, 0.95, 0.97);

varying vec3 zone_col;
varying vec3 wpos;

void vertex() {
	zone_col = CUSTOM0.rgb;
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float shore = smoothstep(0.9, 1.0, UV.x) * 0.6;
	vec3 c = mix(COLOR.rgb, shore_color, shore);
	vec3 col = world_info(c, 1.0);
	if (info_layer == 3) {
		col = mix(col, zone_col, info_mode * step(0.001, zone_col.r + zone_col.g + zone_col.b));
	}
	ALBEDO = world_grid(col, wpos, distance(wpos, CAMERA_POSITION_WORLD));
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}
