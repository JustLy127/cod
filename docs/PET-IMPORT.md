# 在另一个 Codex 账号使用苏苏宠物

动画和主题安装完成后，宠物仍需在目标账号导入一次。宠物库属于账号，复制旧电脑的缓存或安装文件夹不会创建新账号的宠物记录。

## 导入现成精灵图

1. 在目标电脑的 Codex 中登录你准备使用的账号。
2. 确认该账号能够使用 Work 模式的宠物功能，以及 Work Pets 插件的宠物导入能力；如未安装插件，先在客户端添加 Work Pets。功能是否可用以目标账号和客户端提供的选项为准。
3. 开一个聊天，选择 Work 模式，将安装目录内的 `TushanSusuPet\compatible\spritesheet.webp` 作为附件发送给助手；也可以提供这张文件在新电脑的完整路径。
4. 发送下面这段要求：

   > 请使用 Work Pets，把我提供的现成 v2 精灵图导入当前账号，命名为“涂山苏苏”并选为当前宠物。保留原画，不重新生成；保留作者 legeling 署名和 CC BY-NC 4.0 非商业许可。导入后告诉我是否已选中。

5. 导入成功后，到 **设置 → Mini 与宠物** 确认当前选择为苏苏，并打开 Mini。客户端版本不同，设置名称或位置可能不同。

这是现成的 v2 素材，不需要重新画宠物。Windows 安装脚本不会登录账号、调用私有接口或自动上传云端宠物；导入由当前已登录账号的 Work Pets 功能完成。如目标账号没有该功能，本地动画和主题仍可使用，宠物需要等该账号具备导入能力后再添加。

## 同账号与重复导入

同一个账号先查看已有宠物列表；如果已经有苏苏，直接选择并打开 Mini。另一个账号导入后会生成自己的宠物记录，这是正常情况。未确认列表确实没有苏苏前，不必反复导入。

若助手无法读取本地完整路径，直接将 `compatible\spritesheet.webp` 拖入该聊天作为附件。不要发送账号凭据、旧电脑的登录缓存或其他个人配置。

## 素材来源

作者：[legeling](https://github.com/legeling)。原始素材：[awesome-codex-pet / tushan-susu--legeling](https://github.com/legeling/awesome-codex-pet/tree/022aea52f75841d31f57b7596479ebb425480a50/pets/tushan-susu--legeling)。

`compatible/spritesheet.webp` 是已验证的兼容图；只清空闲置行未使用的重复格，全部 73 个有效格的原画像素保持一致。原始图、`pet.json`、提交说明和许可保留在 `TushanSusuPet/package/`。来源与哈希见 `TushanSusuPet/SOURCE.json`。

宠物素材采用 **CC BY-NC 4.0**，保留署名，仅限非商业使用。角色属于原权利人。导入其他账号不改变这些许可条件。
