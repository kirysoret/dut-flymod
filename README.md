# FlyMod 0.2 Beta — Drive Up Together

Mod de BepInEx 6 (IL2CPP) con menú en el juego.

| Tecla (por defecto) | Acción |
|---|---|
| 8 | Abrir / cerrar el menú (se puede mover arrastrando) |
| F8 | Fly on/off (WASD, Espacio sube, Ctrl baja, Shift rápido) |
| G | Voltereta (front flip / back flip), termina horizontal |
| H | Truco aéreo: sube, da volteretas avanzando y cae horizontal |
| B | Boost (mantener) |
| N | Ver al siguiente jugador (espectador) |

Desde el menú se ajustan: velocidad del fly, velocidad/salto/vueltas de la voltereta, altura/distancia/volteretas/velocidad del truco aéreo (con tiempo estimado), potencia del motor, velocidad máxima, tracción y boost (escribiendo en la caja se puede pasar del máximo del slider), y se reasignan todas las teclas (pestaña Teclas: pulsas el botón y luego la tecla nueva).

Con el menú abierto la cámara no se mueve con el mouse.

**TP:** ir a otro jugador, a coordenadas X/Y/Z, o guardar una posición y volver a ella.

**Espectador:** la cámara se pone detrás del coche de otro jugador (mouse para girar, rueda para acercar); si se reinicia, lo sigue.

**Mod de mapas:** agrega el mapa gratis **Infinito (Mod)** a la lista de mapas del juego: piso gris con cuadrícula infinito y nada más. Todos los jugadores de la partida necesitan el mod para jugarlo. En ese mapa nunca se sube puntuación.

**Cualquier auto en cualquier mapa:** todos los mapas pasan a "All cars" (se puede apagar en el menú, pestaña Mapa).

**Al usar cualquier truco se bloquean hasta cerrar el juego:** subida al leaderboard, logros/estadísticas de Steam, recompensas de altura y monedas.

## Actualización

`version.txt` indica la última versión y los enlaces de descarga. El instalador (`FlyModInstaller.exe`) lo comprueba al abrirse y muestra **NEW VERSION** cuando hay una nueva.

- Mod: [FlyMod.dll](https://raw.githubusercontent.com/kirysoret/dut-flymod/main/FlyMod.dll)
- Instalador: [FlyModInstaller.exe](https://raw.githubusercontent.com/kirysoret/dut-flymod/main/FlyModInstaller.exe)

Código fuente en `src/`.
