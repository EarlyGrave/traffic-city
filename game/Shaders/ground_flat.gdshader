// Плоскость земли (docs/design/visual-style.md §2): цвет из Palette.cs, тонирование инфо-режима. Заменяется слоем землепользования (VS9).
shader_type spatial;
render_mode diffuse_lambert, specular_disabled, depth_draw_opaque, shadows_disabled;

#include "res://Shaders/world.gdshaderinc"

uniform vec3 ground_color : source_color = vec3(0.9, 0.9, 0.9);

varying vec3 wpos;

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	ALBEDO = world_grid(world_info(ground_color, 1.0), wpos, distance(wpos, CAMERA_POSITION_WORLD));
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}
