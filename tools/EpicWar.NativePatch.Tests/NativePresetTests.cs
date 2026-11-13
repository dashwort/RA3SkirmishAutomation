using EpicWar.Automation;using System.Text;using System.Buffers.Binary;
internal static class NativePresetTests { internal static void Run(Action<bool,string> check) {
var input=Encoding.UTF8.GetBytes("Rts:GameInfo = M=283data/maps/official/map_mp_2_feasel4;MC=0;MS=0;SD=-468773303;GSID=1649;GT=0;PC=-1;RU=1 100 10000 0 0 10 0 1 0 0 0 -1 -1 1 ;S=Hplayer,0,0,TT,-1,7,-1,-1,0,1,-1,:CH,-1,7,-1,-1,0,-1:X:X:X:X:;\nRts:Map = data_5Cmaps_5Cofficial_5Cmap_5Fmp_2_5Ffeasel4_5Cmap_5Fmp_2_5Ffeasel4_2Emap\n");var p=NativeSkirmishPreset.Parse(input);int passed=0;
void Check(bool b){if(!b)throw new Exception("Fixture failed");passed++;}
void Reject(Action a){try{a();throw new Exception("Mismatch accepted");}catch(InvalidDataException){passed++;}}
Check(p.GameId==0x1649);Check(p.MapPrefix==0x283);Check(p.Seed==-468773303);Check(p.Rules.Length==14&&p.Rules[2]==10000);
Check(p.Slots.Length==6&&p.Slots[0].Type==6&&p.Slots[1].Type==4&&p.Slots[5].Type==1);
Check(p.Slots[0].Attributes.SequenceEqual(new[]{-1,7,-1,-1,0,1,-1}));
var mem=new Dictionary<uint,byte[]>();var session=new byte[0x100];mem[0x1000]=session;
void U(byte[] b,int o,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o,4),v);
void I(byte[] b,int o,int v)=>BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o,4),v);
void Text(uint address,string text,bool wide){var bytes=(wide?Encoding.Unicode:Encoding.ASCII).GetBytes(text);var storage=new byte[bytes.Length+6];BinaryPrimitives.WriteUInt16LittleEndian(storage.AsSpan(0,2),(ushort)text.Length);bytes.CopyTo(storage,4);mem[address-4]=storage;}
U(session,0x34,0x5000);Text(0x5000,p.Map,false);U(session,0x48,p.MapPrefix);U(session,0x40,p.MapCrc);I(session,0x44,p.MapSize);I(session,0x4c,p.Seed);U(session,0x94,p.GameId);I(session,0x58,p.GameType);session[0xfa]=unchecked((byte)p.Pc);
for(int i=0;i<14;i++)I(session,0x5c+i*4,p.Rules[i]);
for(int i=0;i<6;i++){uint addr=(uint)(0x2000+i*0x100);var b=new byte[0x60];mem[addr]=b;U(session,4+i*4,addr);var slot=p.Slots[i];I(b,4,slot.Type);var offsets=slot.Type==6?new[]{0xc,0x18,0x10,0x1c,0x20,0x40,0x4c}:new[]{0xc,0x18,0x10,0x1c,0x20,0x4c};for(int n=0;n<slot.Attributes.Length;n++)I(b,offsets[n],slot.Attributes[n]);if(slot.Type==6){U(b,0x30,0x6000);Text(0x6000,slot.Name!,true);}}
byte[] Read(uint a,int count){foreach(var item in mem)if(a>=item.Key&&(ulong)a+(uint)count<=(ulong)item.Key+(uint)item.Value.Length)return item.Value.AsSpan((int)(a-item.Key),count).ToArray();throw new Exception("Fixture read outside range");}
p.VerifySession(0x1000,Read);passed++;
I(session,0x4c,1);Reject(()=>p.VerifySession(0x1000,Read));I(session,0x4c,p.Seed);
I(session,0x64,5000);Reject(()=>p.VerifySession(0x1000,Read));I(session,0x64,p.Rules[2]);
foreach(int offset in new[]{0xc,0x18,0x10,0x1c,0x20,0x40,0x4c}){var b=mem[0x2000];int old=BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset,4));I(b,offset,old+1);Reject(()=>p.VerifySession(0x1000,Read));I(b,offset,old);}
I(mem[0x2100],4,5);Reject(()=>p.VerifySession(0x1000,Read));I(mem[0x2100],4,4);
Reject(()=>NativeSkirmishPreset.Parse(Encoding.UTF8.GetBytes("Rts:GameInfo = x")));
var wrongMap=Encoding.UTF8.GetString(input).Replace("Rts:Map = data", "Rts:Map = wrong");Reject(()=>NativeSkirmishPreset.Parse(Encoding.UTF8.GetBytes(wrongMap)));
mem[0xCEDE30]=new byte[4];U(mem[0xCEDE30],0,0x7000);mem[0x7000]=new byte[0x30];U(mem[0x7000],0x24,0x8000);U(mem[0x7000],0x28,0x8024);mem[0x8000]=new byte[36];
for(int i=0;i<9;i++){uint w=(uint)(0x9000+0x20*i),d=(uint)(0xa000+0x20*i);mem[w]=new byte[4];U(mem[w],0,d);mem[d]=new byte[8];U(mem[d],4,(uint)(i==7?3:i==1?2:i==3?4:1));U(mem[0x8000],4*i,w);}
p.VerifyTemplates(Read);passed++;
var ai=p.Slots[1];int faction=ai.Attributes[1];ai.Attributes[1]=1;Reject(()=>p.VerifyTemplates(Read));ai.Attributes[1]=3;Reject(()=>p.VerifyTemplates(Read));ai.Attributes[1]=99;Reject(()=>p.VerifyTemplates(Read));ai.Attributes[1]=faction;
var human=p.Slots[0];faction=human.Attributes[1];human.Attributes[1]=1;p.VerifyTemplates(Read);passed++;human.Attributes[1]=3;Reject(()=>p.VerifyTemplates(Read));human.Attributes[1]=faction;
check(passed == 25, "Complete-preset schema, effective fields and template fixture checks");

} }
