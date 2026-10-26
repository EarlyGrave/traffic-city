// Точка вместо ТС вдали (docs/design/render.md §3.4): горизонтальный квад 1x1, масштаб инстанса = (длина, 1, ширина).
// Размер в мире = max(истинный размер, k_px пикселей на этой дистанции): ТС не мельче k_px и не крупнее себя - геометрия не раздувается.
// 1 px в мире на дистанции d = 2·d / (высота_вьюпорта · P[1][1]); при FOV 60° и 1080p это 0.00107·d (формула спеки).
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque, shadows_disabled;

uniform float k_px = 2.0;

void vertex() {
	float d = length((MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz);
	float min_size = k_px * 2.0 * d / (VIEWPORT_SIZE.y * PROJECTION_MATRIX[1][1]);
	float lx = max(length(MODEL_MATRIX[0].xyz), 1e-4);
	float lz = max(length(MODEL_MATRIX[2].xyz), 1e-4);
	VERTEX.x *= max(1.0, min_size / lx);
	VERTEX.z *= max(1.0, min_size / lz);
}

void fragment() {
	ALBEDO = COLOR.rgb;
}
