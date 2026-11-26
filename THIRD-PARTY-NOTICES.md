# Благодарности и сторонние источники

Код этого проекта написан независимо. Протоколы устройств описаны в открытых проектах и документациях, на которые мы опирались как на описание формата обмена (сами файлы или код оттуда не копировались):

- **Logitech G435 LIGHTSPEED (донгл 046D:0ACB)**: последовательность входа в служебный режим, формат команд PA и разбор ответа описаны в проектах deddroid/logitech-g435-battery, fr0styJS/g435-lightspeed-battery (лицензия MIT) и в HaloBattery (pull request о поддержке G435).
- **Мышь AJAZZ AJ159 APEX / Angry Miao AM Infinity 8K**: запрос статуса и расположение заряда в отчёте описаны в HaloBattery (HeyOkay) и AJAZZ Control Center.
- **Мыши Compx/Pulsar (VXE, ATK, VGN, Pulsar)**: формат кадров отчёта 0x08 и команды питания описаны в проектах OpenMouse-Project/mouse-protocol, BatteryTray, драйвере hid-pulsar для Linux и в HaloBattery.
- **Logitech HID++**: открытые описания проектов Solaar и libratbag.
- **HidSharp**: библиотека для работы с HID-устройствами (лицензия Apache 2.0).

Названия устройств и торговые марки принадлежат их владельцам. Проект не связан с этими компаниями.
