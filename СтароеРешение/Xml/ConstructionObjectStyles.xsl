<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:ct="urn:BimHouse:CommonDataType" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:cfg="urn:BimHouse:ObjectUtilConfig" xmlns:msxsl="urn:schemas-microsoft-com:xslt" xmlns:bf="urn:BimHouse:XslFunctions">
	<xsl:import href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/BimHouseFunctions.xsl"/>

	<cfg:Config>
		<TagPrefixes>
			<TagPrefix key="Улица" value="ул.&#160;"/>
			<TagPrefix key="Дом" value="д.&#160;"/>
			<TagPrefix key="Помещение" value="оф.&#160;"/>
			<TagPrefix key="Телефон" value="тел.:&#160;"/>
			<TagPrefix key="Email" value="e&#8209;mail:&#160;"/>
			<TagPrefix key="Сайт" value="web:&#160;"/>
		</TagPrefixes>
	</cfg:Config>

	<!--<xsl:template match="*[string(namespace-uri-from-QName(QName(@xsi:type,.))) = 'urn:BimHouse:CommonDataType' and starts-with(string(local-name-from-QName(QName(@xsi:type,.))),'Тип.Базовый.ОбъектСтроительства')]" mode="presenting">-->
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.Базовый.ОбъектСтроительства')]" mode="presenting">
		<xsl:element name="{name()}" namespace="{namespace-uri()}">
			<xsl:copy-of select="@*"/>
			<xsl:apply-templates select="./node()" mode="#current"/>
			<xsl:element name="ct:Представления" namespace="urn:BimHouse:CommonDataType">
				<xsl:element name="ct:НаименованиеСПолнымАдресом" namespace="urn:BimHouse:CommonDataType">
					<xsl:call-template name="CreateObjectTextPresentaionType1">
						<xsl:with-param name="Object" select="."/>
					</xsl:call-template>
				</xsl:element>
			</xsl:element>
		</xsl:element>
	</xsl:template>

	<xsl:template name="CreateObjectTextPresentaionType1">
		<xsl:param name="Object"/>
		<xsl:value-of select="$Object/ct:Наименование"/>
		<xsl:text>, расположенный по адресу: </xsl:text>
		<xsl:variable name="Address">
			<xsl:apply-templates select="$Object/ct:Адрес" mode="presenting"/>
		</xsl:variable>
		<xsl:value-of select="$Address/*/ct:Представления/ct:ПолныйАдрес"/>
	</xsl:template>

</xsl:stylesheet>