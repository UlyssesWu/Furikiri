

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo
// ReSharper disable CommentTypo

namespace Furikiri.Emit
{
    //TJS只有一个flag，判别true或false
    //!0=true 0=false
    //%obj.*name *为直接引用（direct）
    //%obj.%name %为间接引用（indirect）
    //算术移位补符号位，逻辑移位补0

    //%-1 = this
    //%-2 = this proxy (this ?? global)
    //%-3开始，先param后local
    
    /// <summary>
    /// TJS指令
    /// </summary>
    public enum OpCode : short
    {
        /// <summary>
        /// no operation
        /// 什么也不做
        /// </summary>
        NOP = 0,
        /// <summary>
        /// copy constant value
        /// 将*src指向的常量复制到%dst寄存器中
        /// <example>const %dst, *src</example>
        /// </summary>
        CONST = 1,
        /// <summary>
        /// copy register
        /// 将%src寄存器的值复制到%dst寄存器中
        /// <example>cp %dst, %src</example>
        /// </summary>
        CP = 2,
        /// <summary>
        /// clear register
        /// 将%dst寄存器设为void
        /// <example>cl %dst</example>
        /// </summary>
        CL = 3,
        /// <summary>
        /// continue clear register
        /// 范围寄存器置空，从%low到%high
        /// 实际为%low(低址), %range(向上加range个)
        /// <example>ccl %low-%high</example>
        /// </summary>
        CCL = 4,
        /// <summary>
        /// test true
        /// 将flag设为%reg寄存器的bool值
        /// <example>tt %reg</example>
        /// </summary>
        TT = 5,
        /// <summary>
        /// test false
        /// 将flag设为%reg寄存器的bool值取反
        /// <example>tf %reg</example>
        /// </summary>
        TF = 6,
        /// <summary>
        /// compare equal
        /// 判断值相等（==）并设置flag
        /// <example>ceq %reg1, %reg2</example>
        /// </summary>
        CEQ = 7,
        /// <summary>
        /// compare distinct equal
        /// 判断全等（===）并设置flag（即不进行数据类型转换的比较）
        /// <example>cdeq %reg1, %reg2</example>
        /// </summary>
        CDEQ = 8,
        /// <summary>
        /// compare littler than
        /// %reg1 大于 %reg2 则为true
        /// <example>clt %reg1, %reg2</example>
        /// </summary>
        CLT = 9,
        /// <summary>
        /// compare greater than
        /// %reg1 小于 %reg2 则为true
        /// <example>cgt %reg1, %reg2</example>
        /// </summary>
        CGT = 10,
        /// <summary>
        /// set flag
        /// 将%dest设置为flag
        /// <example>setf %dest</example>
        /// </summary>
        SETF = 11,
        /// <summary>
        /// set not flag
        /// 将%dest设置为flag取反
        /// <example>setnf %dest</example>
        /// </summary>
        SETNF = 12,
        /// <summary>
        /// logical not
        /// 逻辑非
        /// <example>lnot %reg</example>
        /// </summary>
        LNOT = 13,
        /// <summary>
        /// not flag
        /// flag取反
        /// <example>nf</example>
        /// </summary>
        NF = 14,
        /// <summary>
        /// jump if flag
        /// 如果flag为true则跳到ip
        /// <example>jf ip</example>
        /// </summary>
        JF = 15,
        /// <summary>
        /// jump if not flag
        /// 如果flag为false则跳到ip
        /// <example>jnf ip</example>
        /// </summary>
        JNF = 16,
        /// <summary>
        /// jump
        /// 跳转
        /// <example>jmp ip</example>
        /// </summary>
        JMP = 17,
        /// <summary>
        /// increment
        /// 自增
        /// <example>inc %reg</example>
        /// </summary>
        INC = 18,
        /// <summary>
        /// increment
        /// 自增（直接引用）
        /// 如果res不为0（号），结果也复制到%res
        /// <example>incpd %res, %obj.*name</example>
        /// </summary>
        INCPD = 19,
        /// <summary>
        /// increment
        /// 自增（间接引用）
        /// 如果res不为0（号），结果也复制到%res
        /// <example>incpi %res, %obj.%name</example>
        /// </summary>
        INCPI = 20,
        /// <summary>
        /// increment
        /// 自增（属性）
        /// 如果res不为0（号），结果也复制到%res
        /// <example>incp %res, %propobj</example>
        /// </summary>
        INCP = 21,
        /// <summary>
        /// decrement
        /// 自减
        /// <example>dec %reg</example>
        /// </summary>
        DEC = 22,
        /// <summary>
        /// decrement
        /// 自减（直接引用）
        /// 如果res不为0（号），结果也复制到%res
        /// <example>decpd %res, %obj.*name</example>
        /// </summary>
        DECPD = 23,
        /// <summary>
        /// decrement
        /// 自减（间接引用）
        /// 如果res不为0（号），结果也复制到%res
        /// <example>decpi %res, %obj.%name</example>
        /// </summary>
        DECPI = 24,
        /// <summary>
        /// decrement
        /// 自减（属性）
        /// 如果res不为0（号），结果也复制到%res
        /// <example>decp %res, %propobj</example>
        /// </summary>
        DECP = 25,
        /// <summary>
        /// logical or
        /// 逻辑或
        /// 结果存储在%dest
        /// <example>lor %dest, %src</example>
        /// </summary>
        LOR = 26,
        /// <summary>
        /// logical or
        /// 逻辑或（直接引用）
        /// %obj.*name和%src运算，结果存储在%obj.*name。如果res不为0（号），结果也复制到%res
        /// <example>lorpd %res, %obj.*name, %src</example>
        /// </summary>
        LORPD = 27,
        /// <summary>
        /// logical or
        /// 逻辑或（间接引用）
        /// %obj.%name和%src运算，结果存储在%obj.%name。如果res不为0（号），结果也复制到%res
        /// <example>lorpi %res, %obj.%name, %src</example>
        /// </summary>
        LORPI = 28,
        /// <summary>
        /// logical or
        /// 逻辑或（属性）
        /// %propobj和%src运算，结果存储在%propobj。如果res不为0（号），结果也复制到%res
        /// <example>lorp %res, %propobj, %src</example>
        /// </summary>
        LORP = 29,
        /// <summary>
        /// logical and
        /// 逻辑与
        /// <example>land %dest, %src</example>
        /// </summary>
        LAND = 30,
        /// <summary>
        /// logical and
        /// 逻辑与
        /// <example>landpd %res, %obj.*name, %src</example>
        /// </summary>
        LANDPD = 31,
        /// <summary>
        /// logical and
        /// 逻辑与
        /// <example>landpi %res, %obj.%name, %src</example>
        /// </summary>
        LANDPI = 32,
        /// <summary>
        /// logical and
        /// 逻辑与
        /// <example>landp %res, %propobj, %src</example>
        /// </summary>
        LANDP = 33,
        /// <summary>
        /// bitwise or
        /// 按位或
        /// <example>bor %dest, %src</example>
        /// </summary>
        BOR = 34,
        /// <summary>
        /// bitwise or
        /// 按位或
        /// <example>borpd %res, %obj.*name, %src</example>
        /// </summary>
        BORPD = 35,
        /// <summary>
        /// bitwise or
        /// 按位或
        /// <example>borpi %res, %obj.%name, %src</example>
        /// </summary>
        BORPI = 36,
        /// <summary>
        /// bitwise or
        /// 按位或
        /// <example>borp %res, %propobj, %src</example>
        /// </summary>
        BORP = 37,
        /// <summary>
        /// bitwise xor
        /// 按位异或
        /// <example>bxor %dest, %src</example>
        /// </summary>
        BXOR = 38,
        /// <summary>
        /// bitwise xor
        /// 按位异或
        /// <example>bxorpd %res, %obj.*name, %src</example>
        /// </summary>
        BXORPD = 39,
        /// <summary>
        /// bitwise xor
        /// 按位异或
        /// <example>bxorpi %res, %obj.%name, %src</example>
        /// </summary>
        BXORPI = 40,
        /// <summary>
        /// bitwise xor
        /// 按位异或
        /// <example>bxorp %res, %propobj, %src</example>
        /// </summary>
        BXORP = 41,
        /// <summary>
        /// bitwise and
        /// 按位与
        /// <example>band %dest, %src</example>
        /// </summary>
        BAND = 42,
        /// <summary>
        /// bitwise and
        /// 按位与
        /// <example>bandpd %res, %obj.*name, %src</example>
        /// </summary>
        BANDPD = 43,
        /// <summary>
        /// bitwise and
        /// 按位与
        /// <example>bandpi %res, %obj.%name, %src</example>
        /// </summary>
        BANDPI = 44,
        /// <summary>
        /// bitwise and
        /// 按位与
        /// <example>bandp %res, %propobj, %src</example>
        /// </summary>
        BANDP = 45,
        /// <summary>
        /// shift arithmetic right
        /// 算术右移
        /// 把%dest算术右移%src位，结果储存在%dest中
        /// <example>sar %dest, %src</example>
        /// </summary>
        SAR = 46,
        /// <summary>
        /// shift arithmetic right
        /// 算术右移
        /// 把%obj.*name算术右移%src位，结果储存在%obj.*name中
        /// <example>sarpd %res, %obj.*name, %src</example>
        /// </summary>
        SARPD = 47,
        /// <summary>
        /// shift arithmetic right
        /// 算术右移
        /// 把%obj.%name算术右移%src位，结果储存在%obj.%name中
        /// <example>sarpi %res, %obj.%name, %src</example>
        /// </summary>
        SARPI = 48,
        /// <summary>
        /// shift arithmetic right
        /// 算术右移
        /// 把%propobj算术右移%src位，结果储存在%propobj中
        /// <example>sarp %res, %propobj, %src</example>
        /// </summary>
        SARP = 49,
        /// <summary>
        /// shift arithmetic left
        /// 算术左移
        /// 把%dest算术左移%src位，结果储存在%dest中
        /// <example>sal %dest, %src</example>
        /// </summary>
        SAL = 50,
        /// <summary>
        /// shift arithmetic left
        /// 算术左移
        /// 把%dest算术左移%src位，结果储存在%dest中
        /// <example>salpd %res, %obj.*name, %src</example>
        /// </summary>
        SALPD = 51,
        /// <summary>
        /// shift arithmetic left
        /// 算术左移
        /// 把%obj.%name算术左移%src位，结果储存在%obj.%name中
        /// <example>salpi %res, %obj.%name, %src</example>
        /// </summary>
        SALPI = 52,
        /// <summary>
        /// shift arithmetic left
        /// 算术左移
        /// 把%propobj算术左移%src位，结果储存在%propobj中
        /// <example>salp %res, %propobj, %src</example>
        /// </summary>
        SALP = 53,
        /// <summary>
        /// shift bitwise right
        /// 按位右移
        /// 把%dest右移%src位，结果储存在%dest中
        /// <example>sr %dest, %src</example>
        /// </summary>
        SR = 54,
        /// <summary>
        /// shift bitwise right
        /// 按位右移
        /// 把%obj.*name右移%src位，结果储存在%obj.*name中
        /// <example>srpd %res, %obj.*name, %src</example>
        /// </summary>
        SRPD = 55,
        /// <summary>
        /// shift bitwise right
        /// 按位右移
        /// 把%obj.%name右移%src位，结果储存在%obj.%name中
        /// <example>srpi %res, %obj.%name, %src</example>
        /// </summary>
        SRPI = 56,
        /// <summary>
        /// shift bitwise right
        /// 按位右移
        /// 把%propobj右移%src位，结果储存在%propobj中
        /// <example>srp %res, %propobj, %src</example>
        /// </summary>
        SRP = 57,
        /// <summary>
        /// add
        /// 加
        /// 结果存储在%dest
        /// <example>add %dest, %src</example>
        /// </summary>
        ADD = 58,
        /// <summary>
        /// add
        /// 加
        /// <example>addpd %res, %obj.*name, %src</example>
        /// </summary>
        ADDPD = 59,
        /// <summary>
        /// add
        /// 加
        /// <example>addpi %res, %obj.%name, %src</example>
        /// </summary>
        ADDPI = 60,
        /// <summary>
        /// add
        /// 加
        /// <example>addp %res, %propobj, %src</example>
        /// </summary>
        ADDP = 61,
        /// <summary>
        /// subtract
        /// 减
        /// 结果存储在%dest
        /// <example>sub %dest, %src</example>
        /// </summary>
        SUB = 62,
        /// <summary>
        /// subtract
        /// 减
        /// <example>subpd %res, %obj.*name, %src</example>
        /// </summary>
        SUBPD = 63,
        /// <summary>
        /// subtract
        /// 减
        /// <example>subpi %res, %obj.%name, %src</example>
        /// </summary>
        SUBPI = 64,
        /// <summary>
        /// subtract
        /// 减
        /// <example>subp %res, %propobj, %src</example>
        /// </summary>
        SUBP = 65,
        /// <summary>
        /// modulo
        /// 取余
        /// <example>mod %dest, %src</example>
        /// </summary>
        MOD = 66,
        /// <summary>
        /// modulo
        /// 取余
        /// <example>modpd %res, %obj.*name, %src</example>
        /// </summary>
        MODPD = 67,
        /// <summary>
        /// modulo
        /// 取余
        /// <example>modpi %res, %obj.%name, %src</example>
        /// </summary>
        MODPI = 68,
        /// <summary>
        /// modulo
        /// 取余
        /// <example>modp %res, %propobj, %src</example>
        /// </summary>
        MODP = 69,
        /// <summary>
        /// real divide
        /// 实数除
        /// <example>div %dest, %src</example>
        /// </summary>
        DIV = 70,
        /// <summary>
        /// real divide
        /// 实数除
        /// <example>divpd %res, %obj.*name, %src</example>
        /// </summary>
        DIVPD = 71,
        /// <summary>
        /// real divide
        /// 实数除
        /// <example>divpi %res, %obj.%name, %src</example>
        /// </summary>
        DIVPI = 72,
        /// <summary>
        /// real divide
        /// 实数除
        /// <example>divp %res, %propobj, %src</example>
        /// </summary>
        DIVP = 73,
        /// <summary>
        /// integer divide
        /// 整除
        /// <example>idiv %dest, %src</example>
        /// </summary>
        IDIV = 74,
        /// <summary>
        /// integer divide
        /// 整除
        /// <example>idivpd %res, %obj.*name, %src</example>
        /// </summary>
        IDIVPD = 75,
        /// <summary>
        /// integer divide
        /// 整除
        /// <example>idivpi %res, %obj.%name, %src</example>
        /// </summary>
        IDIVPI = 76,
        /// <summary>
        /// integer divide
        /// 整除
        /// <example>idivp %res, %propobj, %src</example>
        /// </summary>
        IDIVP = 77,
        /// <summary>
        /// multiply
        /// 乘
        /// <example>mul %dest, %src</example>
        /// </summary>
        MUL = 78,
        /// <summary>
        /// multiply
        /// 乘
        /// <example>mulpd %res, %obj.*name, %src</example>
        /// </summary>
        MULPD = 79,
        /// <summary>
        /// multiply
        /// 乘
        /// <example>mulpi %res, %obj.%name, %src</example>
        /// </summary>
        MULPI = 80,
        /// <summary>
        /// multiply
        /// 乘
        /// <example>mulp %res, %propobj, %src</example>
        /// </summary>
        MULP = 81,
        /// <summary>
        /// bitwise not
        /// 按位非
        /// <example>bnot %reg</example>
        /// </summary>
        BNOT = 82,
        /// <summary>
        /// check type
        /// 获取类型字符串
        /// <example>typeof %reg</example>
        /// </summary>
        TYPEOF = 83,
        /// <summary>
        /// check type
        /// 获取类型字符串
        /// <example>typeofd %reg, %obj.*name</example>
        /// </summary>
        TYPEOFD = 84,
        /// <summary>
        /// check type
        /// 获取类型字符串
        /// <example>typeofi %reg, %obj.%name</example>
        /// </summary>
        TYPEOFI = 85,
        /// <summary>
        /// evaluate expression
        /// 执行表达式（有结果）
        /// 结果存储于%reg
        /// <example>eval %reg</example>
        /// </summary>
        EVAL = 86,
        /// <summary>
        /// execute expression
        /// 执行表达式（无结果）
        /// 丢弃结果
        /// <example>eexp %reg</example>
        /// </summary>
        EEXP = 87,
        /// <summary>
        /// check instance
        /// 检查实例归属
        /// 如果%reg的对象的类型是%classname（字符串），则将%reg设为true，反之为false
        /// <example>chkins %reg, %classname</example>
        /// </summary>
        CHKINS = 88,
        /// <summary>
        /// make ascii string
        /// 将%reg中数字转换为1个字符
        /// <example>asc %reg</example>
        /// </summary>
        ASC = 89,
        /// <summary>
        /// character code
        /// 将%reg中字符串的第一个字转换为编码
        /// <example>chr %reg</example>
        /// </summary>
        CHR = 90,
        /// <summary>
        /// number
        /// 将%reg中的对象转换为数值
        /// <example>num %reg</example>
        /// </summary>
        NUM = 91,
        /// <summary>
        /// change sign
        /// 改变正负号
        /// <example>chs %reg</example>
        /// </summary>
        CHS = 92,
        /// <summary>
        /// invalidate
        /// 使%reg中的对象无效化
        /// <example>inv %reg</example>
        /// </summary>
        INV = 93,
        /// <summary>
        /// check invalidate
        /// 检查是否无效，结果置于%reg
        /// <example>chkinv %reg</example>
        /// </summary>
        CHKINV = 94,
        /// <summary>
        /// convert to integer
        /// 转换为整数
        /// <example>int %reg</example>
        /// </summary>
        INT = 95,
        /// <summary>
        /// convert to real
        /// 转换为实数
        /// <example>real %reg</example>
        /// </summary>
        REAL = 96,
        /// <summary>
        /// convert to string
        /// 转换为字符串
        /// <example>str %reg</example>
        /// </summary>
        STR = 97,
        /// <summary>
        /// convert to octet
        /// 转换为字节数组
        /// <example>octet %reg</example>
        /// </summary>
        OCTET = 98,
        /// <summary>
        /// function call
        /// 函数调用
        /// 当%dest不为0（号）时，存储结果到%dest，否则丢弃结果
        /// <example>call %dest, %func(%arg1, %arg2, %arg3, ...)</example>
        /// </summary>
        CALL = 99,
        /// <summary>
        /// function call
        /// 函数调用
        /// 当%dest不为0（号）时，存储结果到%dest，否则丢弃结果
        /// <example>calld %dest, %obj.*name(%arg1, %arg2, %arg3, ...)</example>
        /// </summary>
        CALLD = 100,
        /// <summary>
        /// function call
        /// 函数调用
        /// 当%dest不为0（号）时，存储结果到%dest，否则丢弃结果
        /// <example>calli %dest, %obj.%name(%arg1, %arg2, %arg3, ...)</example>
        /// </summary>
        CALLI = 101,
        /// <summary>
        /// create new
        /// 创建对象，调用构造函数
        /// <example>new %dest, %func(%arg1, %arg2, %arg3, ...)</example>
        /// </summary>
        NEW = 102,
        /// <summary>
        /// get property direct
        /// get属性（直接）
        /// 调用property handler
        /// <example>gpd %dest, %obj.*name</example>
        /// </summary>
        GPD = 103,
        /// <summary>
        /// set property direct
        /// set属性（直接）
        /// 将%obj.*name设置为%src的值
        /// <example>spd %obj.*name, %src</example>
        /// </summary>
        SPD = 104,
        /// <summary>
        /// set property direct
        /// set属性（直接）
        /// 将%obj.*name设置为%src的值，如果%obj.*name不存在则新建
        /// <example>spde %obj.*name, %src</example>
        /// </summary>
        SPDE = 105,
        /// <summary>
        /// set property direct
        /// set属性（直接）
        /// 将%obj.*name设置为%src的值，将%obj.*name设为隐藏成员（当前版本无意义）
        /// <example>spdeh %obj.*name, %src</example>
        /// </summary>
        SPDEH = 106,
        /// <summary>
        /// get property indirect
        /// get属性（间接）
        /// <example>gpi %dest, %obj.%name</example>
        /// </summary>
        GPI = 107,
        /// <summary>
        /// set property indirect
        /// set属性（间接）
        /// 将%obj.%name设置为%src的值
        /// <example>spi %obj.%name, %src</example>
        /// </summary>
        SPI = 108,
        /// <summary>
        /// set property indirect
        /// set属性（间接）
        /// 将%obj.%name设置为%src的值，如果%obj.*name不存在则新建
        /// <example>spie %obj.%name, %src</example>
        /// </summary>
        SPIE = 109,
        /// <summary>
        /// get property direct
        /// get属性（直接）
        /// 不调用property handler
        /// <example>gpds %dest, %obj.*name</example>
        /// </summary>
        GPDS = 110,
        /// <summary>
        /// set property direct
        /// set属性（直接）
        /// 将%obj.*name设置为%src的值，不调用property handler
        /// <example>spds %obj.*name, %src</example>
        /// </summary>
        SPDS = 111,
        /// <summary>
        /// get property indirect
        /// get属性（间接），不调用property handler
        /// <example>gpis %dest, %obj.%name</example>
        /// </summary>
        GPIS = 112,
        /// <summary>
        /// set property indirect
        /// set属性（间接）
        /// 将%obj.%name设置为%src的值，不调用property handler
        /// <example>spis %obj.%name, %src</example>
        /// </summary>
        SPIS = 113,
        /// <summary>
        /// set property
        /// set属性
        /// <example>setp %propobj, %reg</example>
        /// </summary>
        SETP = 114,
        /// <summary>
        /// get property
        /// get属性
        /// <example>getp %reg, %propobj</example>
        /// </summary>
        GETP = 115,
        /// <summary>
        /// delete member
        /// 删除对象
        /// 若%reg不为0（号），则将是否成功（bool）存储到%reg
        /// <example>deld %reg, %obj.*name</example>
        /// </summary>
        DELD = 116,
        /// <summary>
        /// delete member
        /// 删除对象
        /// 若%reg不为0（号），则将是否成功（bool）存储到%reg
        /// <example>deli %reg, %obj.%name</example>
        /// </summary>
        DELI = 117,
        /// <summary>
        /// set result value
        /// 设置返回值
        /// 将%reg的值设置为返回值
        /// <example>srv %reg</example>
        /// </summary>
        SRV = 118,
        /// <summary>
        /// return
        /// <example>ret</example>
        /// </summary>
        RET = 119,
        /// <summary>
        /// enter try block
        /// 进入try块
        /// 如发生异常，则跳转到ip，并将异常对象设置到%reg
        /// <example>entry ip, %reg</example>
        /// </summary>
        ENTRY = 120,
        /// <summary>
        /// exit from try block
        /// 退出try块
        /// <example>extry</example>
        /// </summary>
        EXTRY = 121,
        /// <summary>
        /// throw exception object
        /// 抛出异常
        /// 将%reg作为异常对象抛出
        /// <example>throw %reg</example>
        /// </summary>
        THROW = 122,
        /// <summary>
        /// change this
        /// %dest で表されたオブジェクトのクロージャ部分を、%src で示されたオブジェクトに変更します。
        /// <example>chgthis %dest, %src</example>
        /// </summary>
        CHGTHIS = 123,
        /// <summary>
        /// global
        /// 获取全局对象
        /// <example>global %dest</example>
        /// </summary>
        GLOBAL = 124,
        /// <summary>
        /// add class instance information
        /// 将%info所表示的实例信息添加到%dest 
        /// <example>addci %dest, %info</example>
        /// </summary>
        ADDCI = 125,
        /// <summary>
        /// register members
        /// 注册成员
        /// 向this对象中注册成员
        /// <example>regmember</example>
        /// </summary>
        REGMEMBER = 126,
        /// <summary>
        /// call debugger
        /// 中断执行，调用调试器
        /// <example>debugger</example>
        /// </summary>
        DEBUGGER = 127,

        LAST = 128,

        PreDec = 129,

        PostInc = 130,

        PostDec = 131,

        Delete = 132,

        FuncCall = 133,

        IgnorePropGet = 134,

        IgnorePropSet = 135,

        //TypeOf = 136,
    }
}
