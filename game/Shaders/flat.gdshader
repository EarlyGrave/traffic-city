// Unshaded-слой цветов вершин (зебры, docs/design/visual-style.md §2, §7): множитель light_tint (ночью не светятся) и тонирование инфо-режима.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque, shadows_disabled;

#include "res://Shaders/world.gdshaderinc"

void fragment() {
	ALBEDO = world_info(COLOR.rgb * light_tint, 1.0);
}
