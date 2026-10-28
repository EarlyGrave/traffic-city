// Общее для всех шейдеров мира (docs/design/visual-style.md §7): глобальные uniform и тонирование инфо-режима.
// Значения пишет InfoMode.cs (RenderingServer.GlobalShaderParameterSet); объявлены в project.godot [shader_globals].
// Ни одного цвета здесь нет: беж инфо-режима и light_tint приходят из Palette.cs.
global uniform float info_mode;      // 0..1: переход основной вид -> инфо-режим (0.25 с)
global uniform int info_layer;       // 1 Трафик, 2 Пешеходы, 3 Зоны
global uniform vec3 light_tint;      // множитель для unshaded-материалов (VS10: ночью темнее); днём (1, 1, 1)
global uniform float cam_focus_dist; // расстояние камеры до точки фокуса, м (для затухания рёбер в шейдерах VS2+)
global uniform vec3 info_bg_dark;    // беж инфо-режима для тёмных исходных цветов (линейное пространство)
global uniform vec3 info_bg_light;   // и для светлых
global uniform float grid_on;        // RP10: 0..1 показ мировой сетки (Shift+G), 0 - шейдер не считает линии
global uniform vec4 grid_params;     // RP10: x - module_m (м), y/z - начало сетки (м), w - линия-блок каждые w клеток (block_m / module_m)
global uniform vec3 grid_color;      // RP10: цвет линий сетки (Palette.GridLine)

// Всё приглушено в светлый беж по яркости исходного цвета; оверлей слоя накладывается ПОСЛЕ этого вызова (насыщенные цвета поверх беж).
// w - доля тонирования (0 - не тонировать: stuck/выделенные ТС остаются насыщенными).
vec3 world_info(vec3 c, float w) {
	float l = clamp(dot(c, vec3(0.2126, 0.7152, 0.0722)), 0.0, 1.0);
	return mix(c, mix(info_bg_dark, info_bg_light, l), info_mode * w);
}

// Мировая сетка module_m (RP10, road-profiles.md §9): линии по мировым координатам XZ, тонкая каждые module_m, жирнее каждые w клеток (блок).
// Рисуется в шейдере земли: 0 draw calls. Гаснет с дистанцией (по метрам и по размеру клетки в пикселях, чтобы не рябила).
// wp - мировая точка поверхности, cam_d - расстояние от камеры до неё, м.
vec3 world_grid(vec3 col, vec3 wp, float cam_d) {
	if (grid_on < 0.001) {
		return col;
	}
	vec2 u = (wp.xz - grid_params.yz) / grid_params.x;   // координаты в клетках
	vec2 fw = max(fwidth(u), vec2(1e-5));
	vec2 gm = abs(fract(u - 0.5) - 0.5) / fw;             // расстояние до тонкой линии, px
	float minor = 1.0 - clamp(min(gm.x, gm.y) - 0.35, 0.0, 1.0);
	vec2 ub = u / grid_params.w;
	vec2 fb = max(fwidth(ub), vec2(1e-5));
	vec2 gb = abs(fract(ub - 0.5) - 0.5) / fb;
	float major = 1.0 - clamp(min(gb.x, gb.y) - 0.6, 0.0, 1.0);
	float cell_px = 1.0 / max(fw.x, fw.y);                // размер клетки на экране, px
	float k_minor = smoothstep(3.0, 9.0, cell_px) * (1.0 - smoothstep(350.0, 700.0, cam_d));
	float k_major = smoothstep(6.0, 18.0, cell_px * grid_params.w) * (1.0 - smoothstep(2500.0, 4500.0, cam_d));
	float a = max(minor * k_minor * 0.22, major * k_major * 0.55) * grid_on;
	return mix(col, grid_color, a);
}
