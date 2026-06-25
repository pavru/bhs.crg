<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:ct="urn:BimHouse:CommonDataType" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:cfg="urn:BimHouse:OrgUtilConfig" xmlns:msxsl="urn:schemas-microsoft-com:xslt" xmlns:bf='urn:BimHouse:XslFunctions'>

	<xsl:import href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/BimHouseFunctions.xsl"/>
	<xsl:output method="xml" version="1.0" encoding="UTF-8" indent="yes"/>
	
	<!--<xsl:template match="*[starts-with(@xsi:type,'ct:Тип.Базовый.Организация')]" mode="presenting">-->
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.Базовый.ПерсонаИлиОрганизация')]" mode="presenting">
		<xsl:element name="{name()}" namespace="{namespace-uri()}">
			<xsl:copy-of select="@*"/>
			<xsl:apply-templates select="./node()" mode="presenting"/>
			<xsl:element name="ct:Представления" namespace="urn:BimHouse:CommonDataType">
				<xsl:element name="ct:Ответственный" namespace="urn:BimHouse:CommonDataType">
					<xsl:choose>
						<xsl:when test="./ct:Лицо/ct:Персона">
							<xsl:value-of select="./ct:Лицо/ct:Персона/ct:ФИО/ct:Фамилия"/>
							<xsl:text>&#160;</xsl:text>
							<xsl:value-of select="./ct:Лицо/ct:Персона/ct:ФИО/ct:Инициалы"/>
							<xsl:text> </xsl:text>
							<xsl:value-of select="./ct:Лицо/ct:Персона/ct:Должность"/>
							<xsl:text> </xsl:text>
							<xsl:value-of select="./ct:Лицо/ct:Персона/ct:Организация/ct:Наименование/ct:Краткое"/>
						</xsl:when>
						<xsl:when test="./ct:Лицо/ct:Организация">
							<xsl:value-of select="./ct:Лицо/ct:Организация/ct:Наименование/ct:Краткое"/>
						</xsl:when>
						<xsl:otherwise> </xsl:otherwise>
					</xsl:choose>
				</xsl:element>
			</xsl:element>
		</xsl:element>
	</xsl:template>

</xsl:stylesheet>